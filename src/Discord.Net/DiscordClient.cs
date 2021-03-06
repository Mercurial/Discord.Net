﻿using Discord.API;
using Discord.API.Models;
using Discord.Helpers;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Discord
{
	/// <summary> Provides a connection to the DiscordApp service. </summary>
	public partial class DiscordClient
	{
		private readonly JsonHttpClient _http;
		private readonly DiscordAPI _api;
		private readonly DiscordDataSocket _webSocket;
#if !DNXCORE50
		private readonly DiscordVoiceSocket _voiceWebSocket;
#endif
		private readonly JsonSerializer _serializer;
		private readonly Regex _userRegex, _channelRegex;
		private readonly MatchEvaluator _userRegexEvaluator, _channelRegexEvaluator;
		private readonly ManualResetEventSlim _blockEvent;
		private readonly Random _rand;
		private readonly ConcurrentQueue<Message> _pendingMessages;
		private readonly DiscordClientConfig _config;

		private volatile Task _mainTask;
		protected volatile string _myId, _sessionId;

		/// <summary> Returns the User object for the current logged in user. </summary>
		public User User => _user;
		private User _user;

		/// <summary> Returns true if the user has successfully logged in and the websocket connection has been established. </summary>
		public bool IsConnected => _isConnected;
		private bool _isConnected;

		/// <summary> Returns true if this client was requested to disconnect. </summary>
		public bool IsClosing => _disconnectToken.IsCancellationRequested;
		/// <summary> Returns a cancel token that is triggered when a disconnect is requested. </summary>
		public CancellationToken CloseToken => _disconnectToken.Token;
		private volatile CancellationTokenSource _disconnectToken;

		internal bool IsDebugMode => _isDebugMode;
		private bool _isDebugMode;

#if !DNXCORE50
		public Server CurrentVoiceServer => _currentVoiceToken != null ? _servers[_currentVoiceServerId] : null;
		private string _currentVoiceServerId, _currentVoiceToken;
#endif

		//Constructor
		/// <summary> Initializes a new instance of the DiscordClient class. </summary>
		public DiscordClient(DiscordClientConfig config = null)
		{
			_blockEvent = new ManualResetEventSlim(true);
			_config = config ?? new DiscordClientConfig();
			_isDebugMode = config.EnableDebug;
			_rand = new Random();
			
			_serializer = new JsonSerializer();
#if TEST_RESPONSES
			_serializer.CheckAdditionalContent = true;
			_serializer.MissingMemberHandling = MissingMemberHandling.Error;
#endif

			_userRegex = new Regex(@"<@\d+?>", RegexOptions.Compiled);
			_channelRegex = new Regex(@"<#\d+?>", RegexOptions.Compiled);
			_userRegexEvaluator = new MatchEvaluator(e =>
			{
				string id = e.Value.Substring(2, e.Value.Length - 3);
				var user = _users[id];
				if (user != null)
					return '@' + user.Name;
				else //User not found
					return e.Value;
			});
			_channelRegexEvaluator = new MatchEvaluator(e =>
			{
				string id = e.Value.Substring(2, e.Value.Length - 3);
				var channel = _channels[id];
				if (channel != null)
					return channel.Name;
				else //Channel not found
					return e.Value;
			});

			if (_config.UseMessageQueue)
				_pendingMessages = new ConcurrentQueue<Message>();

			_http = new JsonHttpClient(config.EnableDebug);
			_api = new DiscordAPI(_http);
			if (_isDebugMode)
				_http.OnDebugMessage += (s, e) => RaiseOnDebugMessage(e.Type, e.Message);

			CreateCaches();

			_webSocket = new DiscordDataSocket(this, config.ConnectionTimeout, config.WebSocketInterval, config.EnableDebug);
			_webSocket.Connected += (s, e) => RaiseConnected();
			_webSocket.Disconnected += async (s, e) =>
			{
				RaiseDisconnected();

				//Reconnect if we didn't cause the disconnect
				if (e.WasUnexpected)
				{
					while (!_disconnectToken.IsCancellationRequested)
					{
						try
						{
							await Task.Delay(_config.ReconnectDelay);
							await _webSocket.ReconnectAsync();
							if (_http.Token != null)
								await _webSocket.Login(_http.Token);
							break;
						}
						catch (Exception ex)
						{
							RaiseOnDebugMessage(DebugMessageType.Connection, $"DataSocket reconnect failed: {ex.Message}");
							//Net is down? We can keep trying to reconnect until the user runs Disconnect()
							await Task.Delay(_config.FailedReconnectDelay);
						}
					}
				}
			};
			if (_isDebugMode)
				_webSocket.OnDebugMessage += (s, e) => RaiseOnDebugMessage(e.Type, $"DataSocket: {e.Message}");

#if !DNXCORE50
			if (_config.EnableVoice)
			{
				_voiceWebSocket = new DiscordVoiceSocket(this, _config.VoiceConnectionTimeout, _config.WebSocketInterval, config.EnableDebug);
				_voiceWebSocket.Connected += (s, e) => RaiseVoiceConnected();
				_voiceWebSocket.Disconnected += async (s, e) =>
				{
					RaiseVoiceDisconnected();

					//Reconnect if we didn't cause the disconnect
					if (e.WasUnexpected)
					{
						while (!_disconnectToken.IsCancellationRequested)
						{
							try
							{
								await Task.Delay(_config.ReconnectDelay);
								await _voiceWebSocket.ReconnectAsync();
								await _voiceWebSocket.Login(_currentVoiceServerId, _myId, _sessionId, _currentVoiceToken);
								break;
							}
							catch (Exception ex)
							{
								if (_isDebugMode)
									RaiseOnDebugMessage(DebugMessageType.Connection, $"VoiceSocket reconnect failed: {ex.Message}");
								//Net is down? We can keep trying to reconnect until the user runs Disconnect()
								await Task.Delay(_config.FailedReconnectDelay);
							}
						}
					}
				};
				if (_isDebugMode)
					_voiceWebSocket.OnDebugMessage += (s, e) => RaiseOnDebugMessage(e.Type, $"VoiceSocket: {e.Message}");
			}
#endif

#if !DNXCORE50
			_webSocket.GotEvent += async (s, e) =>
#else
			_webSocket.GotEvent += (s, e) =>
#endif
			{
                switch (e.Type)
				{
					//Global
					case "READY": //Resync
						{
							var data = e.Event.ToObject<TextWebSocketEvents.Ready>(_serializer);

							_servers.Clear();
							_channels.Clear();
							_users.Clear();

							_myId = data.User.Id;
#if !DNXCORE50
							_sessionId = data.SessionId;
#endif
							_user = _users.Update(data.User.Id, data.User);
							foreach (var server in data.Guilds)
								_servers.Update(server.Id, server);
							foreach (var channel in data.PrivateChannels)
								_channels.Update(channel.Id, null, channel);
						}
						break;

					//Servers
					case "GUILD_CREATE":
						{
							var data = e.Event.ToObject<TextWebSocketEvents.GuildCreate>(_serializer);
							var server = _servers.Update(data.Id, data);
							try { RaiseServerCreated(server); } catch { }
						}
						break;
					case "GUILD_UPDATE":
						{
							var data = e.Event.ToObject<TextWebSocketEvents.GuildUpdate>(_serializer);
							var server = _servers.Update(data.Id, data);
							try { RaiseServerUpdated(server); } catch { }
						}
						break;
					case "GUILD_DELETE":
						{
							var data = e.Event.ToObject<TextWebSocketEvents.GuildDelete>(_serializer);
							var server = _servers.Remove(data.Id);
							if (server != null)
								try { RaiseServerDestroyed(server); } catch { }
						}
						break;

					//Channels
					case "CHANNEL_CREATE":
						{
							var data = e.Event.ToObject<TextWebSocketEvents.ChannelCreate>(_serializer);
							var channel = _channels.Update(data.Id, data.GuildId, data);
							try { RaiseChannelCreated(channel); } catch { }
						}
						break;
					case "CHANNEL_UPDATE":
						{
							var data = e.Event.ToObject<TextWebSocketEvents.ChannelUpdate>(_serializer);
							var channel = _channels.Update(data.Id, data.GuildId, data);
							try { RaiseChannelUpdated(channel); } catch { }
						}
						break;
					case "CHANNEL_DELETE":
						{
							var data = e.Event.ToObject<TextWebSocketEvents.ChannelDelete>(_serializer);
							var channel = _channels.Remove(data.Id);
							if (channel != null)
								try { RaiseChannelDestroyed(channel); } catch { }
						}
						break;

					//Members
					case "GUILD_MEMBER_ADD":
						{
							var data = e.Event.ToObject<TextWebSocketEvents.GuildMemberAdd>(_serializer);
							var user = _users.Update(data.User.Id, data.User);
							var server = _servers[data.ServerId];
							if (server != null)
							{
								var member = server.UpdateMember(data);
								try { RaiseMemberAdded(member); } catch { }
							}
						}
						break;
					case "GUILD_MEMBER_UPDATE":
						{
							var data = e.Event.ToObject<TextWebSocketEvents.GuildMemberUpdate>(_serializer);
							var user = _users.Update(data.User.Id, data.User);
							var server = _servers[data.ServerId];
							if (server != null)
							{
								var member = server.UpdateMember(data);
								try { RaiseMemberUpdated(member); } catch { }
							}
						}
						break;
					case "GUILD_MEMBER_REMOVE":
						{
							var data = e.Event.ToObject<TextWebSocketEvents.GuildMemberRemove>(_serializer);
							var server = _servers[data.ServerId];
							if (server != null)
							{
								var member = server.RemoveMember(data.User.Id);
								if (member != null)
									try { RaiseMemberRemoved(member); } catch { }
							}
						}
						break;

					//Roles
					case "GUILD_ROLE_CREATE":
						{
							var data = e.Event.ToObject<TextWebSocketEvents.GuildRoleCreateUpdate>(_serializer);
							var role = _roles.Update(data.Role.Id, data.ServerId, data.Role);
							try { RaiseRoleCreated(role); } catch { }
						}
						break;
					case "GUILD_ROLE_UPDATE":
						{
							var data = e.Event.ToObject<TextWebSocketEvents.GuildRoleCreateUpdate>(_serializer);
							var role = _roles.Update(data.Role.Id, data.ServerId, data.Role);
							try { RaiseRoleUpdated(role); } catch { }
						}
						break;
					case "GUILD_ROLE_DELETE":
						{
							var data = e.Event.ToObject<TextWebSocketEvents.GuildRoleDelete>(_serializer);
							var role = _roles.Remove(data.RoleId);
							if (role != null)
								try { RaiseRoleDeleted(role); } catch { }
						}
						break;

					//Bans
					case "GUILD_BAN_ADD":
						{
							var data = e.Event.ToObject<TextWebSocketEvents.GuildBanAddRemove>(_serializer);
							var user = _users.Update(data.User.Id, data.User);
							var server = _servers[data.ServerId];
							try { RaiseBanAdded(user, server); } catch { }
						}
						break;
					case "GUILD_BAN_REMOVE":
						{
							var data = e.Event.ToObject<TextWebSocketEvents.GuildBanAddRemove>(_serializer);
							var user = _users.Update(data.User.Id, data.User);
							var server = _servers[data.ServerId];
							if (server != null && server.RemoveBan(user.Id))
							{
								try { RaiseBanRemoved(user, server); } catch { }
							}
						}
						break;

					//Messages
					case "MESSAGE_CREATE":
						{
							var data = e.Event.ToObject<TextWebSocketEvents.MessageCreate>(_serializer);
							Message msg = null;
							bool wasLocal = _config.UseMessageQueue && data.Author.Id == _myId && data.Nonce != null;
                            if (wasLocal)
							{
								msg = _messages.Remap("nonce" + data.Nonce, data.Id);
								if (msg != null)
								{
									msg.IsQueued = false;
									msg.Id = data.Id;
								}
							}
							msg = _messages.Update(data.Id, data.ChannelId, data);
							msg.User.UpdateActivity(data.Timestamp);
							if (wasLocal)
							{
								try { RaiseMessageSent(msg); } catch { }
							}
							try { RaiseMessageCreated(msg); } catch { }
						}
						break;
					case "MESSAGE_UPDATE":
						{
							var data = e.Event.ToObject<TextWebSocketEvents.MessageUpdate>(_serializer);
							var msg = _messages.Update(data.Id, data.ChannelId, data);
							try { RaiseMessageUpdated(msg); } catch { }
						}
						break;
					case "MESSAGE_DELETE":
						{
							var data = e.Event.ToObject<TextWebSocketEvents.MessageDelete>(_serializer);
							var msg = GetMessage(data.MessageId);
							if (msg != null)
							{
								_messages.Remove(msg.Id);
								try { RaiseMessageDeleted(msg); } catch { }
							}
						}
						break;
					case "MESSAGE_ACK":
						{
							var data = e.Event.ToObject<TextWebSocketEvents.MessageAck>(_serializer);
							var msg = GetMessage(data.MessageId);
							if (msg != null)
								try { RaiseMessageRead(msg); } catch { }
						}
						break;

					//Statuses
					case "PRESENCE_UPDATE":
						{
							var data = e.Event.ToObject<TextWebSocketEvents.PresenceUpdate>(_serializer);
							var user = _users.Update(data.User.Id, data.User);
							var server = _servers[data.ServerId];
							if (server != null)
							{
								var member = server.UpdateMember(data);
								try { RaisePresenceUpdated(member); } catch { }
							}
						}
						break;
					case "VOICE_STATE_UPDATE":
						{
							var data = e.Event.ToObject<TextWebSocketEvents.VoiceStateUpdate>(_serializer);
							var server = _servers[data.ServerId];
							if (server != null)
							{
								var member = server.UpdateMember(data);
								if (member != null)
									try { RaiseVoiceStateUpdated(member); } catch { }
							}
						}
						break;
					case "TYPING_START":
						{
							var data = e.Event.ToObject<TextWebSocketEvents.TypingStart>(_serializer);
							var channel = _channels[data.ChannelId];
							var user = _users[data.UserId];
							if (user != null)
							{
								user.UpdateActivity(DateTime.UtcNow);
								if (channel != null)
									try { RaiseUserTyping(user, channel); } catch { }
							}
						}
						break;

					//Voice
					case "VOICE_SERVER_UPDATE":
						{
							var data = e.Event.ToObject<TextWebSocketEvents.VoiceServerUpdate>(_serializer);
							var server = _servers[data.ServerId];
							server.VoiceServer = data.Endpoint;
                            try { RaiseVoiceServerUpdated(server, data.Endpoint); } catch { }

#if !DNXCORE50
							if (_config.EnableVoice && data.ServerId == _currentVoiceServerId)
							{
								_currentVoiceToken = data.Token;
								await _voiceWebSocket.ConnectAsync("wss://" + data.Endpoint.Split(':')[0]);
								await _voiceWebSocket.Login(_currentVoiceServerId, _myId, _myId, data.Token);
							}
#endif
						}
						break;

					//Settings
					case "USER_UPDATE":
						{
							var data = e.Event.ToObject<TextWebSocketEvents.UserUpdate>(_serializer);
							var user = _users.Update(data.Id, data);
							try { RaiseUserUpdated(user); } catch { }
						}
						break;
					case "USER_SETTINGS_UPDATE":
						{
							//TODO: Process this
						}
						break;

					//Others
					default:
						RaiseOnDebugMessage(DebugMessageType.WebSocketUnknownEvent, "Unknown WebSocket message type: " + e.Type);
						break;
				}
			};
		}

		//Async
		private async Task MessageQueueLoop()
		{
			var cancelToken = _disconnectToken.Token;
			try
			{
				Message msg;
				while (!cancelToken.IsCancellationRequested)
				{
					while (_pendingMessages.TryDequeue(out msg))
					{
						bool hasFailed = false;
						APIResponses.SendMessage apiMsg = null;
                        try
						{
							apiMsg = await _api.SendMessage(msg.ChannelId, msg.RawText, msg.MentionIds, msg.Nonce);
						}
						catch (WebException) { break; }
						catch (HttpException) { hasFailed = true; }

						if (!hasFailed)
						{
							_messages.Remap("nonce_", apiMsg.Id);
							_messages.Update(msg.Id, msg.ChannelId, apiMsg);
						}
						msg.IsQueued = false;
						msg.HasFailed = hasFailed;
						try { RaiseMessageSent(msg); } catch { }
					}
					await Task.Delay(_config.MessageQueueInterval);
				}
			}
			catch { }
			finally { _disconnectToken.Cancel(); }
		}
		private string GenerateNonce()
		{
			lock (_rand)
				return _rand.Next().ToString();
		}

		//Connection
		/// <summary> Connects to the Discord server with the provided token. </summary>
		public Task<string> Connect(string token)
			=> ConnectInternal(null, null, token);
		/// <summary> Connects to the Discord server with the provided email and password. </summary>
		/// <returns> Returns a token for future connections. </returns>
		public Task<string> Connect(string email, string password)
			=> ConnectInternal(email, password, null);
		/// <summary> Connects to the Discord server with the provided token, and will fall back to username and password. </summary>
		/// <returns> Returns a token for future connections. </returns>
		public Task<string> Connect(string email, string password, string token)
			=> ConnectInternal(email, password, token);
		/// <summary> Connects to the Discord server as an anonymous user with the provided username. </summary>
		/// <returns> Returns a token for future connections. </returns>
		public Task<string> ConnectAnonymous(string username)
			=> ConnectInternal(username, null, null);
		public async Task<string> ConnectInternal(string emailOrUsername, string password, string token)
		{
			bool success = false;
			await Disconnect();
			_blockEvent.Reset();
			_disconnectToken = new CancellationTokenSource();

			string url = (await _api.GetWebSocket()).Url;

			if (token != null)
			{
				try
				{
					//Login using cached token
					await _webSocket.ConnectAsync(url);
					if (_isDebugMode)
						RaiseOnDebugMessage(DebugMessageType.Connection, $"DataSocket connected.");
					_http.Token = token;

					await _webSocket.Login(_http.Token);
					if (_isDebugMode)
						RaiseOnDebugMessage(DebugMessageType.Connection, $"DataSocket got token.");
					success = true;
				}
				catch (InvalidOperationException) //Bad Token
				{
					if (_isDebugMode)
						RaiseOnDebugMessage(DebugMessageType.Connection, $"DataSocket had a bad token.");
					if (password == null) //If we don't have an alternate login, throw this error
						throw;
				}
			}
			if (!success) 
			{
				//Open websocket while we wait for login response
				Task socketTask = _webSocket.ConnectAsync(url);

				if (password != null) //Normal Login
				{
					var response = await _api.Login(emailOrUsername, password);
					if (_isDebugMode)
						RaiseOnDebugMessage(DebugMessageType.Connection, $"DataSocket got token.");
					token = response.Token;
				}
				else //Anonymous login
				{
					var response = await _api.LoginAnonymous(emailOrUsername);
					if (_isDebugMode)
						RaiseOnDebugMessage(DebugMessageType.Connection, $"DataSocket generated anonymous token.");
					token = response.Token;
				}
				_http.Token = token;

				//Wait for websocket to finish connecting, then send token
				await socketTask;
				if (_isDebugMode)
					RaiseOnDebugMessage(DebugMessageType.Connection, $"DataSocket connected.");
				await _webSocket.Login(_http.Token);
				if (_isDebugMode)
					RaiseOnDebugMessage(DebugMessageType.Connection, $"DataSocket logged in.");
				success = true;
			}

			if (success)
			{
				if (_config.UseMessageQueue)
					_mainTask = MessageQueueLoop();
				else
					_mainTask = _disconnectToken.Wait();
				_mainTask = _mainTask.ContinueWith(async x =>
				{
					await _webSocket.DisconnectAsync();
#if !DNXCORE50
					if (_config.EnableVoice)
						await _voiceWebSocket.DisconnectAsync();
#endif

					//Clear send queue
					Message ignored;
					while (_pendingMessages.TryDequeue(out ignored)) { }

					_channels.Clear();
					_messages.Clear();
					_roles.Clear();
					_servers.Clear();
					_users.Clear();

					_blockEvent.Set();
					_mainTask = null;
				}).Unwrap();
				_isConnected = true;
			}
			else
				token = null;
			return token;
		}
		/// <summary> Disconnects from the Discord server, canceling any pending requests. </summary>
		public async Task Disconnect()
		{
			if (_mainTask != null)
			{
				try { _disconnectToken.Cancel(); } catch (NullReferenceException) { }
				try { await _mainTask; } catch (NullReferenceException) { }
			}
		}

		//Helpers
		private void CheckReady()
		{
			if (_blockEvent.IsSet)
				throw new InvalidOperationException("The client is currently disconnecting.");
			else if (!_isConnected)
				throw new InvalidOperationException("The client is not currently connected to Discord");
		}
		internal string CleanMessageText(string text)
		{
			text = _userRegex.Replace(text, _userRegexEvaluator);
			text = _channelRegex.Replace(text, _channelRegexEvaluator);
			return text;
        }

		/// <summary> Blocking call that will not return until client has been stopped. This is mainly intended for use in console applications. </summary>
		public void Block()
		{
			_blockEvent.Wait();
		}
	}
}
