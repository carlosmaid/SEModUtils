﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Equinox.ProceduralWorld.Utils.Session;
using Sandbox.ModAPI;
using VRage.Collections;
using VRage.Game.ModAPI;
using VRage.Utils;

namespace Equinox.Utils.Command
{
    public class MyCommandDispatchComponent : MyLoggingSessionComponent
    {
        private readonly Dictionary<string, MyCommand> m_commands = new Dictionary<string, MyCommand>();

        private static readonly Type[] SuppliedDeps = new[] { typeof(MyCommandDispatchComponent) };
        public override IEnumerable<Type> SuppliesComponents => SuppliedDeps;

        public override void Attach()
        {
            base.Attach();
            lock (m_commands)
                m_commands.Clear();
            if (MyUtilities.IsDecisionMaker)
                MyAPIGateway.Utilities.MessageRecieved += HandleGlobalCommand;
            if (MyUtilities.IsController)
                MyAPIGateway.Utilities.MessageEntered += HandleLocalCommand;
        }

        public override void Detach()
        {
            base.Detach();
            if (MyUtilities.IsDecisionMaker)
                MyAPIGateway.Utilities.MessageRecieved -= HandleGlobalCommand;
            if (MyUtilities.IsController)
                MyAPIGateway.Utilities.MessageEntered -= HandleLocalCommand;
            lock (m_commands)
                m_commands.Clear();
        }

        public void AddCommand(MyCommand c)
        {
            lock (m_commands)
            {
                foreach (var s in c.Names)
                    if (m_commands.ContainsKey(s))
                        throw new ArgumentException("Command with name " + c + " already exists");
                foreach (var s in c.Names)
                    m_commands[s] = c;
            }
        }

        public void RemoveCommand(MyCommand c)
        {
            lock (m_commands)
            {
                foreach (var s in c.Names)
                    m_commands.Remove(s);
            }
        }

        private void HandleLocalCommand(string msg, ref bool sendToOthers)
        {
            if (msg.Length == 0 || msg[0] != '/') return;
            try
            {
                var args = ParseArguments(msg, 1);
                MyCommand cmd;
                lock (m_commands)
                    if (!m_commands.TryGetValue(args[0], out cmd))
                    {
                        Log(MyLogSeverity.Debug, "Unknown command {0}", args[0]);
                        return;
                    }

                var player = MyAPIGateway.Session.Player;
                if (player == null)
                {
                    Log(MyLogSeverity.Warning, "Attempted to run a local command without a player.");
                    return;
                }
                sendToOthers = false;
                if (!cmd.AllowedSessionType.Flagged(MyAPIGateway.Session.SessionType()))
                {
                    Log(MyLogSeverity.Debug, "Unable to run {0} on a session of type {1}; it requires type {2}", args[0], MyAPIGateway.Session.SessionType(), cmd.AllowedSessionType);
                    return;
                }
                if (!cmd.CanPromotionLevelUse(player.PromoteLevel))
                {
                    MyAPIGateway.Utilities.ShowMessage("EqUtils", "You must be at least " + cmd.MinimumLevel + " to use this command");
                    return;
                }
                var result = cmd.Process(args);
                if (result != null)
                    MyAPIGateway.Utilities.ShowMessage("EqUtils", result);
            }
            catch (ArgumentException e)
            {
                Log(MyLogSeverity.Debug, "Failed to parse \"{0}\".  Error:\n{1}", msg, e.ToString());
            }
            catch (Exception e)
            {
                Log(MyLogSeverity.Critical, "Failed to process \"{0}\".  Error:\n{1}", msg, e.ToString());
            }
        }

        private void HandleGlobalCommand(ulong steamID, string msg)
        {
            if (msg.Length == 0 || msg[0] != '/') return;
            try
            {
                var args = ParseArguments(msg, 1);
                MyCommand cmd;
                lock (m_commands)
                    if (!m_commands.TryGetValue(args[0], out cmd))
                    {
                        Log(MyLogSeverity.Debug, "Unknown command {0}", args[0]);
                        return;
                    }

                var player = MyAPIGateway.Players.GetPlayerBySteamId(steamID);
                if (player == null)
                {
                    Log(MyLogSeverity.Warning, "Attempted unable to determine player instance for Steam ID {0}", steamID);
                    return;
                }
                if (!MyAPIGateway.Session.SessionType().Flagged(cmd.AllowedSessionType))
                {
                    Log(MyLogSeverity.Debug, "Unable to run {0} on a session of type {1}; it requires type {2}", args[0], MyAPIGateway.Session.SessionType(), cmd.AllowedSessionType);
                    return;
                }
                if (!cmd.CanPromotionLevelUse(player.PromoteLevel))
                {
                    Log(MyLogSeverity.Debug, "Player {0} ({1}) attempted to run {2} at level {3}", player.DisplayName, player.PromoteLevel, args[0], cmd.MinimumLevel);
                    return;
                }
                var result = cmd.Process(args);
                if (result != null)
                {
                    // TODO communicate result to sender.
                }
            }
            catch (ArgumentException e)
            {
                Log(MyLogSeverity.Debug, "Failed to parse \"{0}\".  Error:\n{1}", msg, e.ToString());
            }
            catch (Exception e)
            {
                Log(MyLogSeverity.Critical, "Failed to process \"{0}\".  Error:\n{1}", msg, e.ToString());
            }
        }

        private static readonly MyConcurrentPool<List<string>> ArgsListPool = new MyConcurrentPool<List<string>>(4);
        private static readonly MyConcurrentPool<StringBuilder> ArgBuilderPool = new MyConcurrentPool<StringBuilder>(4);
        private static string[] ParseArguments(string data, int offset = 0)
        {
            var list = ArgsListPool.Get();
            var builder = ArgBuilderPool.Get();
            try
            {
                list.Clear();
                builder.Clear();
                char? currentQuote = null;

                var escaped = false;
                for (var head = offset; head < data.Length; head++)
                {
                    var ch = data[head];
                    if (ch == '\\' && !escaped)
                    {
                        escaped = true;
                        continue;
                    }
                    if (!escaped && (ch == '\'' || ch == '"'))
                    {
                        if (currentQuote.HasValue && currentQuote == ch)
                            currentQuote = null;
                        else if (!currentQuote.HasValue)
                            currentQuote = ch;
                        continue;
                    }
                    if (escaped)
                        switch (ch)
                        {
                            case '\\':
                            case '\'':
                            case '"':
                            case ' ':
                                break;
                            case 't':
                                ch = '\t';
                                break;
                            case 'n':
                                ch = '\n';
                                break;
                            case 'r':
                                ch = '\r';
                                break;
                            default:
                                throw new ArgumentException("Invalid escape sequence: \\" + ch);
                        }
                    if (!escaped && !currentQuote.HasValue && ch == ' ')
                    {
                        list.Add(builder.ToString());
                        builder.Clear();
                        continue;
                    }
                    builder.Append(ch);
                    escaped = false;
                }
                if (currentQuote.HasValue)
                    throw new ArgumentException("Unclosed quote " + currentQuote.Value);
                if (escaped)
                    throw new ArgumentException("Can't end with an escape sequence");
                list.Add(builder.ToString());
                return list.ToArray();
            }
            finally
            {
                list.Clear();
                builder.Clear();
                ArgsListPool.Return(list);
                ArgBuilderPool.Return(builder);
            }
        }
    }
}