﻿using ECommons.Automation.LegacyTaskManager;
using GatherBuddy.Plugin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using ECommons;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI;
using GatherBuddy.AutoGather.Movement;
using GatherBuddy.Classes;
using GatherBuddy.CustomInfo;
using GatherBuddy.Enums;
using GatherBuddy.Interfaces;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        public AutoGather(GatherBuddy plugin)
        {
            // Initialize the task manager
            TaskManager                            =  new();
            _plugin                                =  plugin;
            _movementController                    =  new OverrideMovement();
            GatherBuddy.UptimeManager.UptimeChange += UptimeChange;
        }

        private void UptimeChange(IGatherable obj)
        {
            GatherBuddy.Log.Verbose($"Timer for {obj.Name[GatherBuddy.Language]} has expired and the item has been removed from memory.");
            TimedNodesGatheredThisTrip.Remove(obj.ItemId);
        }

        private readonly OverrideMovement _movementController;

        private readonly GatherBuddy _plugin;

        public TaskManager TaskManager { get; }

        private bool _enabled { get; set; } = false;

        public unsafe bool Enabled
        {
            get => _enabled;
            set
            {
                if (!value)
                {
                    //Do Reset Tasks
                    var gatheringMasterpiece = (AddonGatheringMasterpiece*)Dalamud.GameGui.GetAddonByName("GatheringMasterpiece", 1);
                    if (gatheringMasterpiece != null && !gatheringMasterpiece->AtkUnitBase.IsVisible)
                    {
                        gatheringMasterpiece->AtkUnitBase.IsVisible = true;
                    }

                    if (IsPathing || IsPathGenerating)
                    {
                        VNavmesh_IPCSubscriber.Path_Stop();
                    }

                    TaskManager.Abort();
                    HasSeenFlag                         = false;
                    HiddenRevealed                      = false;
                    _movementController.Enabled         = false;
                    _movementController.DesiredPosition = Vector3.Zero;
                    ResetNavigation();
                    AutoStatus = "Idle...";
                }

                if (value)
                {
                    UpdateItemsToGather();
                }

                _enabled = value;
            }
        }

        public unsafe void DoAutoGather()
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                if (!NavReady && Enabled)
                {
                    AutoStatus = "Waiting for Navmesh...";
                    return;
                }
            }
            catch (Exception e)
            {
                //GatherBuddy.Log.Error(e.Message);
                AutoStatus = "vnavmesh communication failed. Do you have it installed??";
                return;
            }

            if (_movementController.Enabled)
            {
                AutoStatus = $"Advanced unstuck in progress!";
                AdvancedUnstuckCheck();
                return;
            }

            DoSafetyChecks();
            if (TaskManager.IsBusy)
            {
                //GatherBuddy.Log.Verbose("TaskManager has tasks, skipping DoAutoGather");
                return;
            }

            if (!CanAct)
            {
                AutoStatus = "Player is busy...";
                return;
            }
            Gatherable? targetItem =
                (TimedItemsToGather.Count > 0 ? TimedItemsToGather.FirstOrDefault() : ItemsToGather.FirstOrDefault()) as Gatherable;

            if (targetItem == null)
            {
                if (!_plugin.GatherWindowManager.ActiveItems.Any(i => i.InventoryCount < i.Quantity))
                {
                    AutoStatus         = "No items to gather...";
                    Enabled            = false;
                    CurrentDestination = null;
                    VNavmesh_IPCSubscriber.Path_Stop();
                    return;
                }
                UpdateItemsToGather();
                //GatherBuddy.Log.Warning("No items to gather");
                AutoStatus = "No available items to gather";
                return;
            }

            if (IsGathering && GatherBuddy.Config.AutoGatherConfig.DoGathering)
            {
                AutoStatus = "Gathering...";
                TaskManager.Enqueue(VNavmesh_IPCSubscriber.Path_Stop);
                DoActionTasks(targetItem);
                return;
            }
            
            if (IsPathGenerating)
            {
                AutoStatus = "Generating path...";
                AdvancedUnstuckCheck();
                return;
            }

            if (IsPathing)
            {
                StuckCheck();
                AdvancedUnstuckCheck();
            }

            UpdateItemsToGather();

            var location = GatherBuddy.UptimeManager.BestLocation(targetItem);
            if (location.Location.Territory.Id != Svc.ClientState.TerritoryType || !GatherableMatchesJob(targetItem))
            {
                HasSeenFlag = false;
                TaskManager.Enqueue(VNavmesh_IPCSubscriber.Path_Stop);
                TaskManager.Enqueue(() => MoveToTerritory(location.Location));
                return;
            }

            DoUseConsumablesWithoutCastTime();
            
            var validNodesForItem = targetItem.NodeList.SelectMany(n => n.WorldPositions).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            var matchingNodesInZone = location.Location.WorldPositions.Where(w => validNodesForItem.ContainsKey(w.Key)).SelectMany(w => w.Value)
                .Where(v => !IsBlacklisted(v))
                .OrderBy(v => Vector3.Distance(Player.Position, v))
                .ToList();
            var allNodes = Svc.Objects.Where(o => matchingNodesInZone.Contains(o.Position)).ToList();
            var closeNodes = allNodes.Where(o => o.IsTargetable)
                .OrderBy(o => Vector3.Distance(Player.Position, o.Position));
            if (closeNodes.Any())
            {
                TaskManager.Enqueue(() => MoveToCloseNode(closeNodes.First(n => !IsBlacklisted(n.Position)), targetItem));
                return;
            }

            var selectedNode = matchingNodesInZone.FirstOrDefault(n => !FarNodesSeenSoFar.Contains(n));
            if (selectedNode == Vector3.Zero)
            {
                FarNodesSeenSoFar.Clear();
                GatherBuddy.Log.Verbose($"Selected node was null and far node filters have been cleared");
                return;
            }

            // only Legendary and Unspoiled show marker
            if (ShouldUseFlag && targetItem.NodeType is NodeType.Legendary or NodeType.Unspoiled)
            {
                // marker not yet loaded on game
                if (TimedNodePosition == null)
                {
                    AutoStatus = "Waiting on flag show up";
                    return;
                }
                AutoStatus = "Moving to farming area...";
                selectedNode = matchingNodesInZone
                    .Where(o => Vector2.Distance(TimedNodePosition.Value, new Vector2(o.X, o.Z)) < 10).OrderBy(o
                        => Vector2.Distance(TimedNodePosition.Value, new Vector2(o.X, o.Z))).FirstOrDefault(); 
            }

            if (allNodes.Any(n => n.Position == selectedNode && Vector3.Distance(n.Position, Player.Position) < 150))
            {
                FarNodesSeenSoFar.Add(selectedNode);

                CurrentDestination = null;
                VNavmesh_IPCSubscriber.Path_Stop();
                AutoStatus = "Looking for far away nodes...";
                return;
            }

            TaskManager.Enqueue(() => MoveToFarNode(selectedNode));
            return;


            AutoStatus = "Nothing to do...";
        }

        private void DoSafetyChecks()
        {
            if (VNavmesh_IPCSubscriber.Path_GetAlignCamera())
            {
                GatherBuddy.Log.Warning("VNavMesh Align Camera Option turned on! Forcing it off for GBR operation.");
                VNavmesh_IPCSubscriber.Path_SetAlignCamera(false);
            }
        }
    }
}
