using Microsoft.AspNet.SignalR.Client;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace SpreadBot.Infrastructure
{
    public class SocketClient
    {
        private string _url;
        private HubConnection _hubConnection;
        private IHubProxy _hubProxy;

        public SocketClient(string url)
        {
            _url = url;
            _hubConnection = new HubConnection(_url);
            _hubProxy = _hubConnection.CreateHubProxy("c3");
        }

        public async Task<bool> Connect()
        {
            await _hubConnection.Start();
            return _hubConnection.State == ConnectionState.Connected;
        }

        public async Task<SocketResponse> Authenticate(string apiKey, string apiKeySecret)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var randomContent = $"{ Guid.NewGuid() }";
            var content = string.Join("", timestamp, randomContent);
            var signedContent = CreateSignature(apiKeySecret, content);
            var result = await _hubProxy.Invoke<SocketResponse>(
                "Authenticate",
                apiKey,
                timestamp,
                randomContent,
                signedContent);
            return result;
        }

        public void SetAuthExpiringHandler(string apiKey, string apiKeySecret)
        {
            _hubProxy.On("authenticationExpiring", async () =>
            {
                await Authenticate(apiKey, apiKeySecret);
            });
        }

        private static string CreateSignature(string apiSecret, string data)
        {
            var hmacSha512 = new HMACSHA512(Encoding.ASCII.GetBytes(apiSecret));
            var hash = hmacSha512.ComputeHash(Encoding.ASCII.GetBytes(data));
            return BitConverter.ToString(hash).Replace("-", string.Empty);
        }

        public async Task<List<SocketResponse>> Subscribe(string[] channels)
        {
            return await _hubProxy.Invoke<List<SocketResponse>>("Subscribe", (object)channels);
        }

        public async Task<List<SocketResponse>> Unsubscribe(string[] channels)
        {
            return await _hubProxy.Invoke<List<SocketResponse>>("Unsubscribe", (object)channels);
        }

        public void On(string channel, Action callback)
        {
            _hubProxy.On(channel, callback);
        }

        public void On<T>(string channel, Action<T> callback)
        {
            _hubProxy.On(channel, message =>
            {
                var decoded = DataConverter.Decode<T>(message);

                callback(decoded);
            });
        }

        public class SocketResponse
        {
            public bool success { get; set; }
            public string errorCode { get; set; }
        }
    }
}
