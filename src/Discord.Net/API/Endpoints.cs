﻿namespace Discord.API
{
	internal static class Endpoints
	{
		public static readonly string BaseUrl = "discordapp.com";
		public static readonly string BaseShortUrl = "discord.gg";
		public static readonly string BaseHttps = $"https://{BaseUrl}";
		public static readonly string BaseShortHttps = $"https://{BaseShortUrl}";

		// /api
		public static readonly string BaseApi = $"{BaseHttps}/api";
		//public static readonly string Track = $"{BaseApi}/track";
		public static readonly string Gateway = $"{BaseApi}/gateway";

		// /api/auth
		public static readonly string Auth = $"{BaseApi}/auth";
        public static readonly string AuthFingerprint = $"{Auth}fingerprint";
		public static readonly string AuthRegister = $"{Auth}/register";
		public static readonly string AuthLogin = $"{Auth}/login";
		public static readonly string AuthLogout = $"{Auth}/logout";

		// /api/channels
		public static readonly string Channels = $"{BaseApi}/channels";
		public static string Channel(string channelId) => $"{Channels}/{channelId}";
		public static string ChannelTyping(string channelId) => $"{Channels}/{channelId}/typing";
		public static string ChannelMessages(string channelId) => $"{Channels}/{channelId}/messages";
		public static string ChannelMessages(string channelId, int limit) => $"{Channels}/{channelId}/messages?limit={limit}";
		public static string ChannelMessage(string channelId, string msgId) => $"{Channels}/{channelId}/messages/{msgId}";
		public static string ChannelInvites(string channelId) => $"{Channels}/{channelId}/invites";

		// /api/guilds
		public static readonly string Servers = $"{BaseApi}/guilds";
		public static string Server(string serverId) => $"{Servers}/{serverId}";
		public static string ServerChannels(string serverId) => $"{Servers}/{serverId}/channels";
		public static string ServerMember(string serverId, string userId) => $"{Servers}/{serverId}/members/{userId}";
		public static string ServerBan(string serverId, string userId) => $"{Servers}/{serverId}/bans/{userId}";

		// /api/invites
		public static readonly string Invites = $"{BaseApi}/invite";
		public static string Invite(string id) => $"{Invites}/{id}";

		// /api/users
		public static readonly string Users = $"{BaseApi}/users";
		public static string UserMe => $"{Users}/@me";
        public static string UserChannels(string userId) => $"{Users}/{userId}/channels";
		public static string UserAvatar(string userId, string avatarId) => $"{Users}/{userId}/avatars/{avatarId}.jpg";

		// /api/voice
		public static readonly string Voice = $"{BaseApi}/voice";
		public static readonly string VoiceRegions = $"{Voice}/regions";
		public static readonly string VoiceIce = $"{Voice}/ice";

		//Website
		public static string InviteUrl(string code) => $"{BaseShortHttps}/{code}";
    }
}
