using Neo.Json;
using System.Collections.Concurrent;

namespace Neo.Plugins
{
    public partial class Fairy
    {
        public readonly ConcurrentDictionary<string, FairyEngine> debugSessionToEngine = new();

        [RpcMethod]
        protected virtual JToken ListDebugSnapshots(JArray _params)
        {
            JArray session = new JArray();
            foreach (string s in debugSessionToEngine.Keys)
            {
                session.Add(s);
            }
            return session;
        }

        [RpcMethod]
        protected virtual JObject DeleteDebugSnapshots(JArray _params)
        {
            JObject json = new();
            foreach (var s in _params)
            {
                string session = s.AsString();
                json[session] = debugSessionToEngine.Remove(session, out _);
            }
            return json;
        }
    }
}
