﻿using System;
using System.Collections.Generic;
using System.Text;
using Rampastring.XNAUI;
using ClientCore;
using DTAClient.Online;
using DTAClient.DXGUI.Multiplayer.GameLobby.CTCPHandlers;
using Microsoft.Xna.Framework;
using Rampastring.Tools;
using DTAClient.DXGUI.Generic;
using DTAClient.Online.EventArguments;
using ClientGUI;
using DTAClient.domain.Multiplayer.CnCNet;
using DTAClient.domain.Multiplayer;

namespace DTAClient.DXGUI.Multiplayer.CnCNet
{
    public class CnCNetGameLoadingLobby : GameLoadingLobbyBase
    {
        const double GAME_BROADCAST_CHECK_INTERVAL = 10.0;
        const string NOT_ALL_PLAYERS_PRESENT_CTCP_COMMAND = "NPRSNT";
        const string GET_READY_CTCP_COMMAND = "GTRDY";
        const string FILE_HASH_CTCP_COMMAND = "FHSH";
        const string INVALID_FILE_HASH_CTCP_COMMAND = "IHSH";
        const string TUNNEL_PING_CTCP_COMMAND = "TNLPNG";
        const string OPTIONS_CTCP_COMMAND = "OP";
        const string INVALID_SAVED_GAME_INDEX_CTCP_COMMAND = "ISGI";
        const string START_GAME_CTCP_COMMAND = "START";
        const string PLAYER_READY_CTCP_COMMAND = "READY";

        public CnCNetGameLoadingLobby(WindowManager windowManager, TopBar topBar,
            CnCNetManager connectionManager, TunnelHandler tunnelHandler,
            List<GameMode> gameModes) : base(windowManager, topBar)
        {
            this.connectionManager = connectionManager;
            this.tunnelHandler = tunnelHandler;
            this.gameModes = gameModes;

            ctcpCommandHandlers = new CTCPCommandHandler[]
            {
                new NoParamCTCPHandler(NOT_ALL_PLAYERS_PRESENT_CTCP_COMMAND, HandleNotAllPresentNotification),
                new NoParamCTCPHandler(GET_READY_CTCP_COMMAND, HandleGetReadyNotification),
                new StringCTCPHandler(FILE_HASH_CTCP_COMMAND, HandleFileHashCommand),
                new StringCTCPHandler(INVALID_FILE_HASH_CTCP_COMMAND, HandleCheaterNotification),
                new IntCTCPHandler(TUNNEL_PING_CTCP_COMMAND, HandleTunnelPingNotification),
                new StringCTCPHandler(OPTIONS_CTCP_COMMAND, HandleOptionsMessage),
                new NoParamCTCPHandler(INVALID_SAVED_GAME_INDEX_CTCP_COMMAND, HandleInvalidSaveIndexCommand),
                new StringCTCPHandler(START_GAME_CTCP_COMMAND, HandleStartGameCommand),
                new IntCTCPHandler(PLAYER_READY_CTCP_COMMAND, HandlePlayerReadyRequest)
            };
        }

        CTCPCommandHandler[] ctcpCommandHandlers;

        CnCNetManager connectionManager;

        List<GameMode> gameModes;

        TunnelHandler tunnelHandler;
        CnCNetTunnel tunnel;

        Channel channel;

        IRCColor chatColor;

        string hostName;

        string localGame;

        string gameFilesHash;

        int timerTicks = 0;
        TimeSpan timeSinceGameBroadcast = TimeSpan.Zero;

        bool started;

        DarkeningPanel dp;

        public override void Initialize()
        {
            dp = new DarkeningPanel(WindowManager);
            //WindowManager.AddAndInitializeControl(dp);

            //dp.AddChildWithoutInitialize(this);

            //dp.Alpha = 0.0f;
            //dp.Hide();
            localGame = DomainController.Instance().GetDefaultGame();

            base.Initialize();

            connectionManager.ConnectionLost += ConnectionManager_ConnectionLost;
            connectionManager.Disconnected += ConnectionManager_Disconnected;
        }

        protected override void OnVisibleChanged(object sender, EventArgs args)
        {
            base.OnVisibleChanged(sender, args);

            //if (!Visible)
            //    dp.Hide();
            //else
            //    dp.Show();
        }

        private void ConnectionManager_Disconnected(object sender, EventArgs e)
        {
            Clear();
        }

        private void ConnectionManager_ConnectionLost(object sender, ConnectionLostEventArgs e)
        {
            Clear();
        }

        /// <summary>
        /// Sets up events and information before joining the channel.
        /// </summary>
        public void SetUp(bool isHost, CnCNetTunnel tunnel, Channel channel, 
            string hostName)
        {
            this.channel = channel;
            this.tunnel = tunnel;
            this.hostName = hostName;

            channel.MessageAdded += Channel_MessageAdded;
            channel.UserAdded += Channel_UserAdded;
            channel.UserLeft += Channel_UserLeft;
            channel.UserQuitIRC += Channel_UserQuitIRC;
            channel.CTCPReceived += Channel_CTCPReceived;

            if (isHost)
                timerTicks = 1000000;

            Refresh(isHost);
        }

        /// <summary>
        /// Clears event subscriptions and leaves the channel.
        /// </summary>
        public void Clear()
        {
            if (channel != null)
            {
                channel.Leave();

                channel.MessageAdded -= Channel_MessageAdded;
                channel.UserAdded -= Channel_UserAdded;
                channel.UserLeft -= Channel_UserLeft;
                channel.UserQuitIRC -= Channel_UserQuitIRC;
            }

            if (Enabled)
            {
                Enabled = false;
                Visible = false;

                base.LeaveGame();
            }

            TopBar.RemovePrimarySwitchable(this);
        }

        private void Channel_CTCPReceived(object sender, ChannelCTCPEventArgs e)
        {
            foreach (CTCPCommandHandler cmdHandler in ctcpCommandHandlers)
            {
                if (cmdHandler.Handle(e.UserName, e.Message))
                    return;
            }

            Logger.Log("Unhandled CTCP command: " + e.Message + " from " + e.UserName);
        }

        /// <summary>
        /// Called when the local user has joined the game channel.
        /// </summary>
        public void OnJoined()
        {
            FileHashCalculator fhc = new FileHashCalculator();
            fhc.CalculateHashes(gameModes);

            if (IsHost)
            {
                connectionManager.SendCustomMessage(new QueuedMessage(
                    string.Format("MODE {0} +klnNs {1} {2}", channel.ChannelName,
                    channel.Password, SGPlayers.Count),
                    QueuedMessageType.SYSTEM_MESSAGE, 50));

                connectionManager.SendCustomMessage(new QueuedMessage(
                    string.Format("TOPIC {0} :{1}", channel.ChannelName,
                    ProgramConstants.CNCNET_PROTOCOL_REVISION + ";" + localGame.ToLower()),
                    QueuedMessageType.SYSTEM_MESSAGE, 50));

                gameFilesHash = fhc.GetCompleteHash();
            }
            else
            {
                channel.SendCTCPMessage(FILE_HASH_CTCP_COMMAND + " " + fhc.GetCompleteHash(), QueuedMessageType.SYSTEM_MESSAGE, 10);

                channel.SendCTCPMessage(TUNNEL_PING_CTCP_COMMAND + " " + tunnel.PingInMs, QueuedMessageType.SYSTEM_MESSAGE, 10);

                if (tunnel.PingInMs < 0)
                    AddNotice(ProgramConstants.PLAYERNAME + " - unknown ping to tunnel server.");
                else
                    AddNotice(ProgramConstants.PLAYERNAME + " - ping to tunnel server: " + tunnel.PingInMs + " ms");
            }

            TopBar.AddPrimarySwitchable(this);
            TopBar.SwitchToPrimary();
        }

        private void Channel_UserAdded(object sender, UserEventArgs e)
        {
            PlayerInfo pInfo = new PlayerInfo();
            pInfo.Name = e.User.Name;

            Players.Add(pInfo);

            if (sndJoinSound != null)
                sndJoinSound.Play();

            BroadcastOptions();
            CopyPlayerDataToUI();
        }

        private void Channel_UserLeft(object sender, UserNameEventArgs e)
        {
            RemovePlayer(e.UserName);
        }

        private void Channel_UserQuitIRC(object sender, UserNameEventArgs e)
        {
            RemovePlayer(e.UserName);
        }

        private void RemovePlayer(string playerName)
        {
            int index = Players.FindIndex(p => p.Name == playerName);

            if (index == -1)
                return;

            if (sndLeaveSound != null)
                sndLeaveSound.Play();

            Players.RemoveAt(index);

            CopyPlayerDataToUI();

            if (!IsHost && playerName == hostName)
            {
                connectionManager.MainChannel.AddMessage(new ChatMessage(null,
                    Color.Yellow, DateTime.Now, "The game host left the game!"));

                Clear();
            }
        }

        private void Channel_MessageAdded(object sender, IRCMessageEventArgs e)
        {
            lbChatMessages.AddMessage(e.Message);

            if (sndMessageSound != null && e.Message.Sender != null)
                sndMessageSound.Play();
        }

        protected override void AddNotice(string message, Color color)
        {
            channel.AddMessage(new ChatMessage(null, color, DateTime.Now, message));
        }

        protected override void BroadcastOptions()
        {
            if (!IsHost)
                return;

            //if (Players.Count > 0)
                Players[0].Ready = true;

            StringBuilder message = new StringBuilder(OPTIONS_CTCP_COMMAND + " ");
            message.Append(ddSavedGame.SelectedIndex);
            message.Append(";");
            foreach (PlayerInfo pInfo in Players)
            {
                message.Append(pInfo.Name);
                message.Append(":");
                message.Append(Convert.ToInt32(pInfo.Ready));
                message.Append(";");
            }
            message.Remove(message.Length - 1, 1);

            channel.SendCTCPMessage(message.ToString(), QueuedMessageType.GAME_SETTINGS_MESSAGE, 10);
        }

        protected override void SendChatMessage(string message)
        {
            if (sndMessageSound != null)
                sndMessageSound.Play();

            channel.SendChatMessage(message, chatColor);
        }

        protected override void RequestReadyStatus()
        {
            channel.SendCTCPMessage(PLAYER_READY_CTCP_COMMAND + " 1", QueuedMessageType.GAME_PLAYERS_READY_STATUS_MESSAGE, 10);
        }

        protected override void GetReadyNotification()
        {
            base.GetReadyNotification();

            TopBar.SwitchToPrimary();

            if (IsHost)
                channel.SendCTCPMessage(GET_READY_CTCP_COMMAND, QueuedMessageType.GAME_GET_READY_MESSAGE, 0);
        }

        protected override void NotAllPresentNotification()
        {
            base.NotAllPresentNotification();

            if (IsHost)
            {
                channel.SendCTCPMessage(NOT_ALL_PLAYERS_PRESENT_CTCP_COMMAND,
                    QueuedMessageType.GAME_NOTIFICATION_MESSAGE, 0);
            }
        }

        #region CTCP Handlers

        private void HandleGetReadyNotification(string sender)
        {
            if (sender != hostName)
                return;

            GetReadyNotification();
        }

        private void HandleNotAllPresentNotification(string sender)
        {
            if (sender != hostName)
                return;

            NotAllPresentNotification();
        }

        private void HandleFileHashCommand(string sender, string fileHash)
        {
            if (!IsHost)
                return;

            if (fileHash != gameFilesHash)
            {
                PlayerInfo pInfo = Players.Find(p => p.Name == sender);

                if (pInfo == null)
                    return;

                pInfo.Verified = true;

                HandleCheaterNotification(hostName, sender); // Might be a bit hacky
            }
        }

        private void HandleCheaterNotification(string sender, string cheaterName)
        {
            if (sender != hostName)
                return;

            AddNotice(cheaterName + " - modified files detected! They could be cheating!", Color.Red);

            if (IsHost)
                channel.SendCTCPMessage(INVALID_FILE_HASH_CTCP_COMMAND + " " + cheaterName, QueuedMessageType.SYSTEM_MESSAGE, 0);
        }

        private void HandleTunnelPingNotification(string sender, int pingInMs)
        {
            if (pingInMs < 0)
                AddNotice(sender + " - unknown ping to tunnel server.");
            else
                AddNotice(sender + " - ping to tunnel server: " + pingInMs + " ms");
        }

        /// <summary>
        /// Handles an options broadcast sent by the game host.
        /// </summary>
        private void HandleOptionsMessage(string sender, string data)
        {
            if (sender != hostName)
                return;

            string[] parts = data.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 1)
                return;

            int sgIndex = Conversions.IntFromString(parts[0], -1);

            if (sgIndex == -1)
                return;

            if (sgIndex >= ddSavedGame.Items.Count)
            {
                AddNotice("The game host has selected an invalid saved game index! " + sgIndex);
                channel.SendCTCPMessage(INVALID_SAVED_GAME_INDEX_CTCP_COMMAND, QueuedMessageType.SYSTEM_MESSAGE, 10);
                return;
            }

            Players.Clear();

            for (int i = 1; i < parts.Length; i++)
            {
                string[] playerAndReadyStatus = parts[i].Split(':');
                if (playerAndReadyStatus.Length < 2)
                    return;

                string playerName = playerAndReadyStatus[0];
                int readyStatus = Conversions.IntFromString(playerAndReadyStatus[1], -1);

                if (string.IsNullOrEmpty(playerName) || readyStatus == -1)
                    return;

                PlayerInfo pInfo = new PlayerInfo();
                pInfo.Name = playerName;
                pInfo.Ready = Convert.ToBoolean(readyStatus);

                Players.Add(pInfo);
            }

            CopyPlayerDataToUI();
        }

        private void HandleInvalidSaveIndexCommand(string sender)
        {
            PlayerInfo pInfo = Players.Find(p => p.Name == sender);

            if (pInfo == null)
                return;

            pInfo.Ready = false;

            AddNotice(pInfo.Name + " does not have the selected saved game on their system!");

            CopyPlayerDataToUI();
        }

        private void HandleStartGameCommand(string sender, string data)
        {
            if (sender != hostName)
                return;

            string[] parts = data.Split(' ');

            int playerCount = parts.Length / 2;

            for (int i = 0; i < playerCount; i++)
            {
                if (parts.Length < i * 2 + 1)
                    return;

                string pName = parts[i * 2];
                string ipAndPort = parts[i * 2 + 1];
                string[] ipAndPortSplit = ipAndPort.Split(':');

                if (ipAndPortSplit.Length < 2)
                    return;

                int port = 0;
                bool success = int.TryParse(ipAndPortSplit[1], out port);
                if (!success)
                    return;

                PlayerInfo pInfo = Players.Find(p => p.Name == pName);

                if (pInfo == null)
                    continue;

                pInfo.Port = port;
            }

            LoadGame();
        }

        private void HandlePlayerReadyRequest(string sender, int readyStatus)
        {
            PlayerInfo pInfo = Players.Find(p => p.Name == sender);

            if (pInfo == null)
                return;

            pInfo.Ready = Convert.ToBoolean(readyStatus);

            CopyPlayerDataToUI();

            if (IsHost)
                BroadcastOptions();
        }

        #endregion

        protected override void HostStartGame()
        {
            AddNotice("Contacting tunnel server..");
            List<int> playerPorts = tunnel.GetPlayerPortInfo(SGPlayers.Count);

            if (playerPorts.Count < Players.Count)
            {
                AddNotice("An error occured while contacting the specified CnCNet tunnel server. Please try using a different tunnel server " +
                    "(accessible through the advanced options in the game creation window).", Color.Yellow);
                return;
            }

            StringBuilder sb = new StringBuilder(START_GAME_CTCP_COMMAND + " ");
            for (int pId = 0; pId < Players.Count; pId++)
            {
                Players[pId].Port = playerPorts[pId];
                sb.Append(Players[pId].Name);
                sb.Append(";");
                sb.Append("0.0.0.0:");
                sb.Append(playerPorts[pId]);
                sb.Append(";");
            }
            sb.Remove(sb.Length - 1, 1);
            channel.SendCTCPMessage(sb.ToString(), QueuedMessageType.SYSTEM_MESSAGE, 9);

            AddNotice("Starting game..");

            started = true;

            LoadGame();
        }

        protected override void WriteSpawnIniAdditions(IniFile spawnIni)
        {
            spawnIni.SetStringValue("Tunnel", "Ip", tunnel.Address);
            spawnIni.SetIntValue("Tunnel", "Port", tunnel.Port);

            PlayerInfo localPlayer = Players.Find(p => p.Name == ProgramConstants.PLAYERNAME);

            if (localPlayer == null)
                return;

            spawnIni.SetIntValue("Settings", "Port", localPlayer.Port);

            for (int i = 1; i < Players.Count; i++)
            {
                string otherName = spawnIni.GetStringValue("Other" + i, "Name", string.Empty);

                if (string.IsNullOrEmpty(otherName))
                    continue;

                PlayerInfo otherPlayer = Players.Find(p => p.Name == otherName);

                if (otherPlayer == null)
                    continue;

                spawnIni.SetIntValue("Other" + i, "Port", otherPlayer.Port);
            }

            base.WriteSpawnIniAdditions(spawnIni);
        }

        protected override void HandleGameProcessExited()
        {
            Clear();

            base.HandleGameProcessExited();
        }

        protected override void LeaveGame()
        {
            Clear();
        }

        public void ChangeChatColor(IRCColor chatColor)
        {
            this.chatColor = chatColor;
            tbChatInput.TextColor = chatColor.XnaColor;
        }

        private void BroadcastGame()
        {
            timeSinceGameBroadcast = TimeSpan.Zero;

            Channel broadcastChannel = connectionManager.GetChannel("#cncnet-" + localGame.ToLower() + "-games");

            if (broadcastChannel == null)
                return;

            timerTicks++;

            if (timerTicks < 3)
                return;

            StringBuilder sb = new StringBuilder("GAME ");
            sb.Append(ProgramConstants.CNCNET_PROTOCOL_REVISION);
            sb.Append(";");
            sb.Append(ProgramConstants.GAME_VERSION);
            sb.Append(";");
            sb.Append(SGPlayers.Count);
            sb.Append(";");
            sb.Append(channel.ChannelName);
            sb.Append(";");
            sb.Append(channel.UIName);
            sb.Append(";");
            if (started || Players.Count == SGPlayers.Count)
                sb.Append("1");
            else
                sb.Append("0");
            sb.Append("0"); // IsCustomPassword
            sb.Append("0"); // Closed
            sb.Append("1"); // IsLoadedGame
            sb.Append("0"); // IsLadder
            sb.Append(";");
            foreach (SavedGamePlayer sgPlayer in SGPlayers)
            {
                sb.Append(sgPlayer.Name);
                sb.Append(",");
            }

            sb.Remove(sb.Length - 1, 1);
            sb.Append(";");
            sb.Append(lblMapNameValue.Text);
            sb.Append(";");
            sb.Append(lblGameModeValue.Text);
            sb.Append(";");
            sb.Append(tunnel.Address);
            sb.Append(";");
            sb.Append(0); // LoadedGameId

            broadcastChannel.SendCTCPMessage(sb.ToString(), QueuedMessageType.SYSTEM_MESSAGE, 20);

            timerTicks = 0;
        }

        public override void Update(GameTime gameTime)
        {
            if (IsHost)
            {
                timeSinceGameBroadcast += gameTime.ElapsedGameTime;

                if (timeSinceGameBroadcast > TimeSpan.FromSeconds(GAME_BROADCAST_CHECK_INTERVAL))
                    BroadcastGame();
            }

            base.Update(gameTime);
        }

        public override string GetSwitchName()
        {
            return "Load Game";
        }
    }
}