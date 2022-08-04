using Neo.IO;
using Neo.IO.Json;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.SmartContract;
using Neo.SmartContract.Native;
using Neo.VM;
using System.Numerics;
using System.Collections.Concurrent;

namespace Neo.Plugins
{
    public partial class Fairy
    {
        readonly ConcurrentQueue<LogEventArgs> logs = new();

        public UInt160 neoScriptHash = UInt160.Parse("0xef4073a0f2b305a38ec4050e4d3d28bc40ea63f5");
        public UInt160 gasScriptHash = UInt160.Parse("0xd2a4cff31913016155e38e474a2c06d08be276cf");
        const byte Native_Prefix_Account = 20;
        const byte Native_Prefix_TotalSupply = 11;

        [RpcMethod]
        protected virtual JObject InvokeFunctionWithSession(JArray _params)
        {
            string session = _params[0].AsString();
            bool writeSnapshot = _params[1].AsBoolean();
            UInt160 script_hash = UInt160.Parse(_params[2].AsString());
            string operation = _params[3].AsString();
            ContractParameter[] args = _params.Count >= 5 ? ((JArray)_params[4]).Select(p => ContractParameter.FromJson(p)).ToArray() : System.Array.Empty<ContractParameter>();
            Signers signers = _params.Count >= 6 ? SignersFromJson((JArray)_params[5], system.Settings) : null;

            byte[] script;
            using (ScriptBuilder sb = new())
            {
                script = sb.EmitDynamicCall(script_hash, operation, args).ToArray();
            }
            return GetInvokeResultWithSession(session, writeSnapshot, script, signers);
        }

        [RpcMethod]
        protected virtual JObject InvokeScriptWithSession(JArray _params)
        {
            string session = _params[0].AsString();
            bool writeSnapshot = _params[1].AsBoolean();
            byte[] script = Convert.FromBase64String(_params[2].AsString());
            Signers signers = _params.Count >= 4 ? SignersFromJson((JArray)_params[3], system.Settings) : null;
            return GetInvokeResultWithSession(session, writeSnapshot, script, signers);
        }

        private void CacheLog(object sender, LogEventArgs logEventArgs)
        {
            logs.Enqueue(logEventArgs);
        }

        private JObject GetInvokeResultWithSession(string session, bool writeSnapshot, byte[] script, Signers signers = null)
        {
            Transaction? tx = signers == null ? null : new Transaction
            {
                Signers = signers.GetSigners(),
                Attributes = System.Array.Empty<TransactionAttribute>(),
                Witnesses = signers.Witnesses,
            };
            ulong timestamp;
            if (!sessionToTimestamp.TryGetValue(session, out timestamp))  // we allow initializing a new session when executing
                sessionToTimestamp[session] = 0;
            FairyEngine oldEngine, newEngine;
            DataCache validSnapshotBase;
            Block block = null;
            logs.Clear();
            FairyEngine.Log += CacheLog;
            if (timestamp == 0)
            {
                if (sessionToEngine.TryGetValue(session, out oldEngine))
                {
                    newEngine = FairyEngine.Run(script, oldEngine.Snapshot.CreateSnapshot(), container: tx, settings: system.Settings, gas: settings.MaxGasInvoke);
                    validSnapshotBase = oldEngine.Snapshot;
                }
                else
                {
                    newEngine = FairyEngine.Run(script, system.StoreView, container: tx, settings: system.Settings, gas: settings.MaxGasInvoke);
                    validSnapshotBase = system.StoreView;
                }
            }
            else
            {
                oldEngine = sessionToEngine[session];
                validSnapshotBase = oldEngine.Snapshot;
                block = CreateDummyBlockWithTimestamp(oldEngine.Snapshot, system.Settings, timestamp: timestamp);
                newEngine = FairyEngine.Run(script, oldEngine.Snapshot.CreateSnapshot(), persistingBlock: block, container: tx, settings: system.Settings, gas: settings.MaxGasInvoke);
            }
            FairyEngine.Log -= CacheLog;
            if (writeSnapshot && newEngine.State == VMState.HALT)
                sessionToEngine[session] = newEngine;

            JObject json = new();

            for (int i = newEngine.Notifications.Count - 1; i >= 0; i--)
            {
                if(newEngine.Notifications[i].EventName == "OracleRequest")
                {
                    int oracleContractId = NativeContract.Oracle.Id;
                    ulong requestId = (ulong)(new BigInteger(newEngine.Snapshot.TryGet(new StorageKey { Id=oracleContractId, Key=new byte[] { 9 } }).Value.ToArray()) - 1);
                    OracleRequest oracleRequest = newEngine.Snapshot.TryGet(new KeyBuilder(oracleContractId, 7).AddBigEndian(requestId)).GetInteroperable<OracleRequest>();
                    //if (!Uri.TryCreate(oracleRequest.Url, UriKind.Absolute, out var uri))
                    //    break;
                    //if (uri.Scheme != "https")
                    //{
                    //    ConsoleHelper.Info($"WARNING: uri scheme {uri.Scheme} not supported by fairy.");
                    //    break;
                    //}
                    JArray oracleRequests;
                    if (!json.ContainsProperty("oraclerequests"))
                    {
                        oracleRequests = new JArray();
                        json["oraclerequests"] = oracleRequests;
                    }
                    else
                    {
                        oracleRequests = (JArray)json["oraclerequests"];
                    }
                    oracleRequests.Add(oracleRequest.ToStackItem(new ReferenceCounter()).ToJson());
                }
            }

            json["script"] = Convert.ToBase64String(script);
            json["state"] = newEngine.State;
            json["gasconsumed"] = newEngine.GasConsumed.ToString();
            json["exception"] = GetExceptionMessage(newEngine.FaultException);
            if(json["exception"] != null)
            {
                string traceback = $"{json["exception"].GetString()}\r\nCallingScriptHash={newEngine.CallingScriptHash}\r\nCurrentScriptHash={newEngine.CurrentScriptHash}\r\nEntryScriptHash={newEngine.EntryScriptHash}\r\n";
                traceback += newEngine.FaultException.StackTrace;
                foreach (Neo.VM.ExecutionContext context in newEngine.InvocationStack)
                {
                    traceback += $"\r\nInstructionPointer={context.InstructionPointer}, OpCode {context.CurrentInstruction.OpCode}, Script Length={context.Script.Length}";
                }
                if(!logs.IsEmpty)
                {
                    traceback += $"\r\n-------Logs-------({logs.Count})";
                }
                foreach (LogEventArgs log in logs)
                {
                    string contractName = NativeContract.ContractManagement.GetContract(newEngine.Snapshot, log.ScriptHash).Manifest.Name;
                    traceback += $"\r\n[{log.ScriptHash}] {contractName}: {log.Message}";
                }
                json["traceback"] = traceback;
            }
            try
            {
                json["stack"] = new JArray(newEngine.ResultStack.Select(p => ToJson(p, settings.MaxIteratorResultItems)));
            }
            catch (InvalidOperationException)
            {
                json["stack"] = "error: invalid operation";
            }
            if (newEngine.State != VMState.FAULT)
            {
                ProcessInvokeWithWalletAndSnapshot(validSnapshotBase, json, signers, block: block);
            }
            return json;
        }

        private void ProcessInvokeWithWalletAndSnapshot(DataCache snapshot, JObject result, Signers signers = null, Block block = null)
        {
            if (fairyWallet == null || signers == null) return;

            Signer[] witnessSigners = signers.GetSigners().ToArray();
            UInt160? sender = signers.Size > 0 ? signers.GetSigners()[0].Account : null;
            if (witnessSigners.Length <= 0) return;

            Transaction tx;
            try
            {
                tx = fairyWallet.MakeTransaction(snapshot.CreateSnapshot(), Convert.FromBase64String(result["script"].AsString()), sender, witnessSigners, maxGas: settings.MaxGasInvoke, persistingBlock: block);
            }
            catch //(Exception e)
            {
                // result["exception"] = GetExceptionMessage(e);
                return;
            }
            ContractParametersContext context = new(snapshot.CreateSnapshot(), tx, system.Settings.Network);
            fairyWallet.Sign(context);
            if (context.Completed)
            {
                tx.Witnesses = context.GetWitnesses();
                byte[] txBytes = tx.ToArray();
                result["tx"] = Convert.ToBase64String(txBytes);
                long networkfee = (fairyWallet ?? new DummyWallet(system.Settings)).CalculateNetworkFee(system.StoreView, txBytes.AsSerializable<Transaction>());
                result["networkfee"] = networkfee.ToString();
            }
            else
            {
                result["pendingsignature"] = context.ToJson();
            }
        }
    }
}
