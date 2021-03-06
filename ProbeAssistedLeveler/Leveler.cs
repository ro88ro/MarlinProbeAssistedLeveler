﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace ProbeAssistedLeveler
{
    public class Leveler
    {
        // === Start User configurable variables
        private const string ComPort = "COM8";
        private const int BaudRate = 115200;

        // Edit this with the coords of the corners to probe
        private static readonly Dictionary<string, Vector2> CornerCoords = new Dictionary<string, Vector2>
        {
            {"Bottom Left", new Vector2(35, 35)},
            {"Bottom Right", new Vector2(215, 35)},
            {"Top Right", new Vector2(215, 210)},
            {"Top Left", new Vector2(35, 210)}
        };

        // Desired minimum difference expressed as an integer. 2 = 0.02mm
        // An integer is used instead of a floating point value due to inaccuracies in floating point comparison
        private const int TargetDiff = 2;

        // How many times to probe for each corner
        private const int NumProbes = 3;

        // Order in which points are probed
        private static readonly List<string> ProbeOrder = new List<string> { "Bottom Left", "Bottom Right", "Top Right", "Top Left" };

        // Speed used when moving probe into position
        private const int SafeMoveSpeed = 100;

        // Speed used when moving around normally
        private const int FastMoveSpeed = 6000;

        // === End User configurable variables

        private readonly SerialPortHandler _serialPortHandler;
        private readonly CommandSender _commandSender;
        private const double FloatTolerance = 0.001;

        public Leveler()
        {
            _serialPortHandler = new SerialPortHandler(ComPort, BaudRate);
            _commandSender = new CommandSender(_serialPortHandler);
        }

        public void LevelBed()
        {
            // === Auto Home and get home coords
            Console.WriteLine("Auto Homing\n");
            var homeCoords = _commandSender.AutoHome();
            //var homeCoords = new Vector3(138.0f, 140.0f, 12.75f);
            var zOffset = _commandSender.GetProbeZOffset();

            var probeResults = GetProbeResults();
            var sortedCorners = SortProbeResults(probeResults);

            Console.WriteLine("\nCorner Diffs:");
            foreach (var corner in sortedCorners)
            {
                Console.WriteLine($"{corner.CornerName} {corner.Diff}");
            }

            Console.WriteLine();

            // Work out at what height the probe triggered for the highest point
            // This will be the reported Z value for the highest probe, plus the INVERSE (* -1) of the Z offset as set in M851 / PROBE_TO_NOZZLE_OFFSET
            // 
            var baseZHeight = sortedCorners.Last().Z + (zOffset * -1);
            for (var i = 0; i < sortedCorners.Count() - 1; i++)
            {
                var corner = sortedCorners[i];
                var diff = GetDiffAsInt(corner.Diff);
                if (diff > TargetDiff)
                { 
                    Console.WriteLine($"Leveling {corner.CornerName}...");
                    var coords = CornerCoords[corner.CornerName];

                    // Move to corner X/Y and Safe Z height
                    _commandSender.Move(moveMode: MoveMode.Absolute, x: coords.X, y: coords.Y, z: homeCoords.Z, speed: FastMoveSpeed);

                    // Deploy probe
                    _commandSender.ResetProbeAlarm();
                    Thread.Sleep(100);
                    _commandSender.RetractProbe();
                    Thread.Sleep(100);
                    _commandSender.DeployProbe();
                    Thread.Sleep(100);

                    // Gradually move down and stop if the probe activates

                    // IsProbeTriggered() updates too slow to use relative move of 0.01 between each check :(

                    //while (Math.Abs(currentHeight - baseZHeight) > FloatTolerance)
                    //{
                    //    _commandSender.Move(moveMode: MoveMode.Relative, z: -0.01f, speed: FastMoveSpeed);
                    //    currentHeight = _commandSender.GetCurrentPosition().Z;
                    //    if (_commandSender.IsProbeTriggered())
                    //    {
                    //        break;
                    //    }
                    //}

                    _commandSender.Move(moveMode: MoveMode.Absolute, z: baseZHeight, speed: SafeMoveSpeed, waitForFinish: false);
                    while (true)
                    {
                        var currentHeight = _commandSender.GetCurrentPosition().Z;
                        diff = GetDiffAsInt(currentHeight, baseZHeight);
                        if (_commandSender.IsProbeTriggered())
                        {
                            Console.WriteLine("Probe triggered while moving into position, aborting...");
                            //_commandSender.EmergencyStop(); // No "ok" response will be received, so app likely to hang if this happens
                            return;
                        }
                        else if (diff <= 0)
                        {
                            break;
                        }
                    }

                    Console.WriteLine($"Wind {corner.CornerName} knob until probe triggers");

                    while (!_commandSender.IsProbeTriggered())
                    {
                        Thread.Sleep(100);
                    }

                }

                Console.WriteLine($"{corner.CornerName} is leveled\n");
            }

            _commandSender.Move(moveMode: MoveMode.Absolute, x: homeCoords.X, y: homeCoords.Y, z: homeCoords.Z, speed: FastMoveSpeed);
            Console.WriteLine("\nAll corners leveled");
        }

        private int GetDiffAsInt(float diff)
        {
            return Convert.ToInt32(diff * 100);
        }

        private int GetDiffAsInt(float current, float target)
        {
            var foo = current - target;
            return Convert.ToInt32((current - target) * 100);
        }

        private Dictionary<string, ProbeResult> GetProbeResults()
        {
            var probeResults = new Dictionary<string, ProbeResult>();
            // === Probe corners
            foreach (var corner in ProbeOrder)
            {
                var coords = CornerCoords[corner];
                Console.WriteLine($"Probing {corner}...");
                _commandSender.Move(moveMode: MoveMode.Absolute, x: coords.X, y: coords.Y, speed: FastMoveSpeed);

                var probeTotal = 0f;
                for (var i = 0; i < NumProbes; i++)
                {
                    var probeValue = _commandSender.DoSingleProbe();
                    probeTotal += probeValue;
                    Console.WriteLine($"Probe value #{i + 1} = {probeValue}");
                }

                probeTotal /= NumProbes;
                probeResults.Add(corner, new ProbeResult(corner, probeTotal));
                Console.WriteLine($"Average: {probeTotal}\n");
            }

            // === Find lowest and highest values
            var highestValue = float.MinValue;
            var lowestValue = float.MaxValue;

            foreach (var probeResult in probeResults.Values)
            {
                if (probeResult.Z <= lowestValue)
                {
                    lowestValue = probeResult.Z;
                }

                if (probeResult.Z >= highestValue)
                {
                    highestValue = probeResult.Z;
                }
            }

            if (Math.Abs(lowestValue - float.MaxValue) < FloatTolerance) throw new Exception("Could not find lowest value");
            if (Math.Abs(highestValue - float.MinValue) < FloatTolerance) throw new Exception("Could not find highest value");

            // === Calculate diffs
            foreach (var probeResult in probeResults.Values)
            {
                probeResult.Diff = (float)Math.Round(highestValue - probeResult.Z, 3);
            }

            return probeResults;
        }

        private static List<ProbeResult> SortProbeResults(Dictionary<string, ProbeResult> probeResults)
        {
            // Sort diff order
            var cornersToSort = new List<string>(ProbeOrder);
            var sortedCorners = new List<ProbeResult>();

            while (cornersToSort.Count > 0)
            {
                var biggestDiff = GetBiggestDiffValue(cornersToSort, probeResults);
                foreach (var probeResult in probeResults.Values)
                {
                    if (!(Math.Abs(probeResult.Diff - biggestDiff) < FloatTolerance)) continue;
                    sortedCorners.Add(probeResult);
                    cornersToSort.Remove(probeResult.CornerName);
                }
            }

            return sortedCorners;
        }

        private static float GetBiggestDiffValue(IEnumerable<string> cornersToCheck, IReadOnlyDictionary<string, ProbeResult> probeResults)
        {
            var biggestDiffValue = float.MinValue;
            foreach (var corner in cornersToCheck)
            {
                if (probeResults[corner].Diff > biggestDiffValue) biggestDiffValue = probeResults[corner].Diff;
            }

            return biggestDiffValue;
        }
    }
}
