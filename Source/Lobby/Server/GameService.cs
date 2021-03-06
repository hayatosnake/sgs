﻿using Sanguosha.Core.Games;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Sanguosha.Core.Network;
using Sanguosha.Core.Players;
using Sanguosha.Core.UI;
using Sanguosha.Lobby.Core;
using System.Net;
using System.Threading;

namespace Sanguosha.Lobby.Server
{
    public class GameService
    {
        public delegate void GameEndCallback(int roomId);
        public static void StartGameService(IPAddress IP, GameSettings setting, int roomId, GameEndCallback callback, out int portNumber)
        {
            int totalNumberOfPlayers = setting.TotalPlayers;
            int timeOutSeconds = setting.TimeOutSeconds;
#if DEBUG
            Trace.Listeners.Clear();

            TextWriterTraceListener twtl = new TextWriterTraceListener(Path.Combine(Directory.GetCurrentDirectory(), AppDomain.CurrentDomain.FriendlyName + DateTime.Now.Hour.ToString() + DateTime.Now.Minute.ToString() + DateTime.Now.Second.ToString() + ".txt"));
            twtl.Name = "TextLogger";
            twtl.TraceOutputOptions = TraceOptions.ThreadId | TraceOptions.DateTime;

            ConsoleTraceListener ctl = new ConsoleTraceListener(false);
            ctl.TraceOutputOptions = TraceOptions.DateTime;

            Trace.Listeners.Add(twtl);
            Trace.Listeners.Add(ctl);
            Trace.AutoFlush = true;
            Trace.WriteLine("Log starting");
            Trace.Listeners.Add(new ConsoleTraceListener());
#endif
            Game game = setting.GameType == GameType.Pk1v1 ? new Pk1v1Game() : new RoleGame();
            game.Settings = setting;
            Sanguosha.Core.Network.Server server;
            server = new Sanguosha.Core.Network.Server(game, totalNumberOfPlayers, IP);
            portNumber = server.IpPort;
            for (int i = 0; i < totalNumberOfPlayers; i++)
            {
                var player = new Player();
                player.Id = i;
                game.Players.Add(player);
                IPlayerProxy proxy;
                proxy = new ServerNetworkProxy(server, i);
                proxy.TimeOutSeconds = timeOutSeconds;
                proxy.HostPlayer = player;
                game.UiProxies.Add(player, proxy);
            }
            GlobalServerProxy pxy = new GlobalServerProxy(game, game.UiProxies);
            pxy.TimeOutSeconds = timeOutSeconds;
            game.GlobalProxy = pxy;
            game.NotificationProxy = new DummyNotificationProxy();

            game.GameServer = server;
            var thread = new Thread(() => 
            {
#if !DEBUG
                try
                {
#endif
                    game.Run();
#if !DEBUG
                }
                catch (Exception)
                {
                }
#endif
                    try
                    {
                        callback(roomId);
                    }
                    catch (Exception)
                    {
                    }
            }) { IsBackground = true };
            thread.Start();
        }

    }
}
