using Neo.Cryptography.ECC;
using Neo.IO;
using Neo.IO.Json;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.SmartContract;
using Neo.SmartContract.Iterators;
using Neo.SmartContract.Manifest;
using Neo.SmartContract.Native;
using Neo.VM;
using Neo.VM.Types;
using System.Numerics;

namespace Neo.Plugins
{
    public partial class Fairy
    {
        [RpcMethod]
        protected virtual JObject VirtualDeploy(JArray _params)
        {
            if (fairyWallet == null)
                throw new Exception("Please open a wallet before deploying a contract.");
            string session = _params[0].AsString();
            NefFile nef = Convert.FromBase64String(_params[1].AsString()).AsSerializable<NefFile>();
            ContractManifest manifest = ContractManifest.Parse(_params[2].AsString());
            ApplicationEngine oldEngine = sessionToEngine.GetValueOrDefault(session, BuildSnapshotWithDummyScript());
            DataCache snapshot = oldEngine.Snapshot;
            byte[] script;
            using (ScriptBuilder sb = new ScriptBuilder())
            {
                sb.EmitDynamicCall(NativeContract.ContractManagement.Hash, "deploy", nef.ToArray(), manifest.ToJson().ToString());
                script = sb.ToArray();
            }
            JObject json = new();
            try
            {
                Transaction tx = fairyWallet.MakeTransaction(snapshot.CreateSnapshot(), script);
                UInt160 hash = SmartContract.Helper.GetContractHash(tx.Sender, nef.CheckSum, manifest.Name);
                sessionToEngine[session] = ApplicationEngine.Run(script, snapshot.CreateSnapshot(), persistingBlock: CreateDummyBlockWithTimestamp(oldEngine.Snapshot, system.Settings, timestamp: sessionToTimestamp.GetValueOrDefault(session, (ulong)0)), container: tx, settings: system.Settings, gas: settings.MaxGasInvoke);
                json[session] = hash.ToString();
            }
            catch (InvalidOperationException ex)
            {
                if (ex.InnerException == null)
                {
                    throw ex;
                }
                if (ex.InnerException.Message.StartsWith("Contract Already Exists: "))
                {
                    json[session] = ex.InnerException.Message[^42..];
                }
                else
                {
                    throw ex;
                }
            }
            return json;
        }

        [RpcMethod]
        protected virtual JObject PutStorageWithSession(JArray _params)
        {
            string session = _params[0].AsString();
            UInt160 contract = UInt160.Parse(_params[1].AsString());
            string keyBase64 = _params[2].AsString();
            byte[] key = Convert.FromBase64String(keyBase64);
            string valueBase64 = _params[3].AsString();
            byte[] value;
            if (valueBase64 == "")
            {
                value = new byte[0] { };
            }
            else
            {
                value = Convert.FromBase64String(valueBase64);
            }

            ApplicationEngine oldEngine = sessionToEngine[session];
            ContractState contractState = NativeContract.ContractManagement.GetContract(oldEngine.Snapshot, contract);
            if (value.Length == 0)
            {
                oldEngine.Snapshot.Delete(new StorageKey { Id=contractState.Id, Key=key });
            }
            else
            {
                oldEngine.Snapshot.Add(new StorageKey { Id=contractState.Id, Key=key }, new StorageItem(value));
            }
            oldEngine.Snapshot.Commit();
            JObject json = new();
            json[keyBase64] = valueBase64;
            return new JObject();
        }

        [RpcMethod]
        protected virtual JObject GetStorageWithSession(JArray _params)
        {
            string session = _params[0].AsString();
            UInt160 contract = UInt160.Parse(_params[1].AsString());
            string keyBase64 = _params[2].AsString();
            byte[] key = Convert.FromBase64String(keyBase64);

            ApplicationEngine oldEngine = sessionToEngine[session];
            ContractState contractState = NativeContract.ContractManagement.GetContract(oldEngine.Snapshot, contract);
            JObject json = new();
            StorageItem item = oldEngine.Snapshot.TryGet(new StorageKey { Id=contractState.Id, Key=key });
            json[keyBase64] = item == null ? null : Convert.ToBase64String(item.Value.ToArray());
            return json;
        }

        [RpcMethod]
        protected virtual JObject SetNeoBalance(JArray _params)
        {
            string session = _params[0].AsString();
            UInt160 account = UInt160.Parse(_params[1].AsString());
            ulong balance = ulong.Parse(_params[2].AsString());
            return SetTokenBalance(session, neoScriptHash, account, balance, Native_Prefix_Account);
        }

        [RpcMethod]
        protected virtual JObject SetGasBalance(JArray _params)
        {
            string session = _params[0].AsString();
            UInt160 account = UInt160.Parse(_params[1].AsString());
            ulong balance = ulong.Parse(_params[2].AsString());
            return SetTokenBalance(session, gasScriptHash, account, balance, Native_Prefix_Account);
        }

        [RpcMethod]
        protected virtual JObject SetNep17Balance(JArray _params)
        {
            string session = _params[0].AsString();
            UInt160 contract = UInt160.Parse(_params[1].AsString());
            UInt160 account = UInt160.Parse(_params[2].AsString());
            ulong balance = ulong.Parse(_params[3].AsString());
            byte prefix = byte.Parse(_params.Count >= 5 ? _params[4].AsString() : "1");
            return SetTokenBalance(session, contract, account, balance, prefix);
        }

        private JObject SetTokenBalance(string session, UInt160 contract, UInt160 account, ulong balance, byte prefixAccount)
        {
            byte[] balanceBytes = BitConverter.GetBytes(balance);
            ApplicationEngine oldEngine = sessionToEngine[session];
            ContractState contractState = NativeContract.ContractManagement.GetContract(oldEngine.Snapshot, contract);
            JObject json = new();
            if (contract == gasScriptHash)
            {
                prefixAccount = Native_Prefix_Account;
                byte[] key = new byte[] { prefixAccount }.Concat(account.ToArray()).ToArray();
                StorageItem storage = oldEngine.Snapshot.GetAndChange(new StorageKey { Id=contractState.Id, Key=key }, () => new StorageItem(new AccountState()));
                AccountState state = storage.GetInteroperable<AccountState>();
                storage = oldEngine.Snapshot.GetAndChange(new StorageKey { Id=contractState.Id, Key=new byte[] { Native_Prefix_TotalSupply } }, () => new StorageItem(BigInteger.Zero));
                storage.Add(balance - state.Balance);
                state.Balance = balance;
                json[Convert.ToBase64String(key)] = Convert.ToBase64String(balanceBytes);
                return json;
            }
            else if (contract == neoScriptHash)
            {
                prefixAccount = Native_Prefix_Account;
                byte[] key = new byte[] { prefixAccount }.Concat(account.ToArray()).ToArray();
                StorageItem storage = oldEngine.Snapshot.GetAndChange(new StorageKey { Id=contractState.Id, Key=key }, () => new StorageItem(new NeoToken.NeoAccountState()));
                NeoToken.NeoAccountState state = storage.GetInteroperable<NeoToken.NeoAccountState>();
                storage = oldEngine.Snapshot.GetAndChange(new StorageKey { Id=contractState.Id, Key=new byte[] { Native_Prefix_TotalSupply } }, () => new StorageItem(BigInteger.Zero));
                storage.Add(balance - state.Balance);
                state.Balance = balance;
                json[Convert.ToBase64String(key)] = Convert.ToBase64String(balanceBytes);
                return json;
            }
            else
            {
                byte[] key = new byte[] { prefixAccount }.Concat(account.ToArray()).ToArray();
                oldEngine.Snapshot.Add(new StorageKey { Id=contractState.Id, Key=key }, new StorageItem(balanceBytes));
                json[Convert.ToBase64String(key)] = Convert.ToBase64String(balanceBytes);
                return json;
            }
        }

        private class Signers : IVerifiable
        {
            private readonly Signer[] _signers;
            public Witness[] Witnesses { get; set; }
            public int Size => _signers.Length;

            public Signers(Signer[] signers)
            {
                _signers = signers;
            }

            public void Serialize(BinaryWriter writer)
            {
                throw new NotImplementedException();
            }

            public void DeserializeUnsigned(BinaryReader reader)
            {
                throw new NotImplementedException();
            }

            public void Deserialize(ref MemoryReader reader)
            {
                throw new NotImplementedException();
            }
            public void DeserializeUnsigned(ref MemoryReader reader)
            {
                throw new NotImplementedException();
            }

            public UInt160[] GetScriptHashesForVerifying(DataCache snapshot)
            {
                return _signers.Select(p => p.Account).ToArray();
            }

            public Signer[] GetSigners()
            {
                return _signers;
            }

            public void SerializeUnsigned(BinaryWriter writer)
            {
                throw new NotImplementedException();
            }
        }

        private JObject GetInvokeResult(byte[] script, Signers signers = null)
        {
            Transaction? tx = signers == null ? null : new Transaction
            {
                Signers = signers.GetSigners(),
                Attributes = System.Array.Empty<TransactionAttribute>(),
                Witnesses = signers.Witnesses,
            };
            using ApplicationEngine engine = ApplicationEngine.Run(script, system.StoreView, container: tx, settings: system.Settings, gas: settings.MaxGasInvoke);
            JObject json = new();
            json["script"] = Convert.ToBase64String(script);
            json["state"] = engine.State;
            json["gasconsumed"] = engine.GasConsumed.ToString();
            json["exception"] = GetExceptionMessage(engine.FaultException);
            try
            {
                json["stack"] = new JArray(engine.ResultStack.Select(p => ToJson(p, settings.MaxIteratorResultItems)));
            }
            catch (InvalidOperationException)
            {
                json["stack"] = "error: invalid operation";
            }
            if (engine.State != VMState.FAULT)
            {
                ProcessInvokeWithWallet(json, signers);
            }
            return json;
        }

        private static JObject ToJson(StackItem item, int max)
        {
            JObject json = item.ToJson();
            if (item is InteropInterface interopInterface && interopInterface.GetInterface<object>() is IIterator iterator)
            {
                JArray array = new();
                while (max > 0 && iterator.Next())
                {
                    array.Add(iterator.Value(null).ToJson());
                    max--;
                }
                json["iterator"] = array;
                json["truncated"] = iterator.Next();
            }
            return json;
        }

        private static Signers SignersFromJson(JArray _params, ProtocolSettings settings)
        {
            var ret = new Signers(_params.Select(u => new Signer()
            {
                Account = AddressToScriptHash(u["account"].AsString(), settings.AddressVersion),
                Scopes = (WitnessScope)Enum.Parse(typeof(WitnessScope), u["scopes"]?.AsString()),
                AllowedContracts = ((JArray)u["allowedcontracts"])?.Select(p => UInt160.Parse(p.AsString())).ToArray(),
                AllowedGroups = ((JArray)u["allowedgroups"])?.Select(p => ECPoint.Parse(p.AsString(), ECCurve.Secp256r1)).ToArray()
            }).ToArray())
            {
                Witnesses = _params
                    .Select(u => new
                    {
                        Invocation = u["invocation"]?.AsString(),
                        Verification = u["verification"]?.AsString()
                    })
                    .Where(x => x.Invocation != null || x.Verification != null)
                    .Select(x => new Witness()
                    {
                        InvocationScript = Convert.FromBase64String(x.Invocation ?? string.Empty),
                        VerificationScript = Convert.FromBase64String(x.Verification ?? string.Empty)
                    }).ToArray()
            };

            // Validate format

            _ = IO.Helper.ToByteArray(ret.GetSigners()).AsSerializableArray<Signer>();

            return ret;
        }

        static string? GetExceptionMessage(Exception exception)
        {
            return exception?.GetBaseException().Message;
        }
    }
}
