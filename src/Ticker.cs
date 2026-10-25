using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using b1;

namespace VisualDodge
{
    // Runs the flash once per frame on the game thread, without hooks. The game instance calls OnTickWithGroup on every
    // entry of its private list TickList (the list of the game's own managers, for example the UI manager). A
    // background thread adds this object once the game instance is ready, and again when the game clears the list.
    // It sets a copy of the list that holds this object, so the game thread never walks a list that changes.
    internal sealed class Ticker : IGameInstObj
    {
        // BGW_TickGroupMask: the post-update-work group, after the frame's game logic.
        private const int PostUpdateWork = 16384;
        // Errors in a row before the mod turns itself off for this game start.
        private const int MaxErrors = 300;

        private static readonly FieldInfo TickListField =
            typeof(BGWGameInstanceCS).GetField("TickList", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly DodgeFlash flash;
        private volatile bool stopping;
        private volatile bool finished;
        private bool firstTick = true;
        private int errorsInRow;
        private int errorsLogged;

        public Ticker(DodgeFlash flash)
        {
            this.flash = flash;
        }

        public bool bTickEnabled => true;

        public int StatId => 0;

        public void OnAttach()
        {
        }

        public void OnInit()
        {
        }

        public void OnAfterInit()
        {
        }

        public bool CanTick() => true;

        public int GetTickGroupMask() => PostUpdateWork;

        public void OnShutdown()
        {
        }

        public void OnPostLoadMap()
        {
        }

        public void Start()
        {
            if (TickListField == null || TickListField.FieldType != typeof(List<IGameInstObj>))
            {
                ModLog.Error("BGWGameInstanceCS.TickList was not found in this game version: the flash stays off.");
                return;
            }
            new Thread(AttachLoop) { IsBackground = true, Name = "VisualDodge" }.Start();
        }

        // The next tick stops the flash; then the background thread takes this object out of the list.
        public void Stop()
        {
            stopping = true;
        }

        public void OnTickWithGroup(float deltaTime, int tickGroup)
        {
            if (finished)
            {
                return;
            }
            if (stopping)
            {
                Finish();
                return;
            }
            if (firstTick)
            {
                firstTick = false;
                ModLog.Info("ticking on the game thread.");
            }
            try
            {
                flash.Frame();
                errorsInRow = 0;
            }
            catch (Exception e)
            {
                errorsInRow++;
                if (errorsLogged < 10)
                {
                    errorsLogged++;
                    ModLog.Error($"frame: {e}");
                }
                if (errorsInRow >= MaxErrors)
                {
                    ModLog.Error($"{MaxErrors} errors in a row: the flash is off until the next game start.");
                    Finish();
                }
            }
        }

        private void Finish()
        {
            try
            {
                flash.Remove();
            }
            catch (Exception e)
            {
                ModLog.Error($"remove: {e.Message}");
            }
            finished = true;
        }

        private void AttachLoop()
        {
            BGWGameInstanceCS attached = null;
            int failures = 0;
            while (true)
            {
                Thread.Sleep(500);
                try
                {
                    if (stopping || finished)
                    {
                        // Give the game thread up to 2 s to stop the flash before this object leaves the list.
                        for (int i = 0; i < 20 && !finished; i++)
                        {
                            Thread.Sleep(100);
                        }
                        if (!ReferenceEquals(attached, null))
                        {
                            SetList(attached, false);
                        }
                        return;
                    }
                    List<BGWGameInstanceCS> instances = BGWGameInstanceCS.GetAllAliveInstance();
                    BGWGameInstanceCS instance = instances != null && instances.Count > 0 ? instances[0] : null;
                    if (ReferenceEquals(instance, null) || !instance.InitGameInstObjDone)
                    {
                        continue;
                    }
                    if (!ReferenceEquals(instance, attached) || !InList(instance))
                    {
                        SetList(instance, true);
                        attached = instance;
                        ModLog.Info("added to the game's tick list.");
                    }
                }
                catch (Exception e)
                {
                    if (failures++ < 5)
                    {
                        ModLog.Error($"tick list: {e.Message}");
                    }
                }
            }
        }

        private bool InList(BGWGameInstanceCS instance)
        {
            return TickListField.GetValue(instance) is List<IGameInstObj> list && list.Contains(this);
        }

        private void SetList(BGWGameInstanceCS instance, bool add)
        {
            List<IGameInstObj> list = (List<IGameInstObj>)TickListField.GetValue(instance);
            List<IGameInstObj> copy = new List<IGameInstObj>(list.Count + 1);
            foreach (IGameInstObj item in list)
            {
                if (!ReferenceEquals(item, this))
                {
                    copy.Add(item);
                }
            }
            if (add)
            {
                copy.Add(this);
            }
            TickListField.SetValue(instance, copy);
        }
    }
}
