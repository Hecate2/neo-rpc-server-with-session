using Neo.Cryptography.ECC;
using Neo.IO;
using Neo.Json;
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
            if (!sessionStringToFairySession.TryGetValue(session, out FairySession testSession))
            {
                testSession = NewTestSession();
                sessionStringToFairySession[session] = testSession;
            }
            DataCache snapshot = testSession.engine.Snapshot;
            byte[] script;
            using (ScriptBuilder sb = new ScriptBuilder())
            {
                sb.EmitDynamicCall(NativeContract.ContractManagement.Hash, "deploy", nef.ToArray(), manifest.ToJson().ToString());
                script = sb.ToArray();
            }
            JObject json = new();
            try
            {
                Block dummyBlock = CreateDummyBlockWithTimestamp(testSession.engine.Snapshot, system.Settings, timestamp: sessionStringToFairySession[session].timestamp);
                Transaction tx = fairyWallet.MakeTransaction(snapshot.CreateSnapshot(), script, persistingBlock: dummyBlock);
                UInt160 hash = SmartContract.Helper.GetContractHash(tx.Sender, nef.CheckSum, manifest.Name);
                sessionStringToFairySession[session].engine = FairyEngine.Run(script, snapshot.CreateSnapshot(), persistingBlock: dummyBlock, container: tx, settings: system.Settings, gas: settings.MaxGasInvoke, oldEngine: sessionStringToFairySession[session].engine);
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
                    throw ex.InnerException;
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
            byte[] value = Convert.FromBase64String(valueBase64);

            FairySession testSession = sessionStringToFairySession[session];
            ContractState contractState = NativeContract.ContractManagement.GetContract(testSession.engine.Snapshot, contract);
            StorageKey storageKey = new StorageKey { Id=contractState.Id, Key=key };
            testSession.engine.Snapshot.Delete(storageKey);
            if (value.Length > 0)
                testSession.engine.Snapshot.Add(new StorageKey { Id=contractState.Id, Key=key }, new StorageItem(value));
            testSession.engine.Snapshot.Commit();
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

            FairyEngine oldEngine = sessionStringToFairySession[session].engine;
            ContractState contractState = NativeContract.ContractManagement.GetContract(oldEngine.Snapshot, contract);
            JObject json = new();
            StorageItem item = oldEngine.Snapshot.TryGet(new StorageKey { Id=contractState.Id, Key=key });
            json[keyBase64] = item == null ? null : Convert.ToBase64String(item.Value.ToArray());
            return json;
        }

        [RpcMethod]
        protected virtual JObject FindStorageWithSession(JArray _params)
        {
            string session = _params[0].AsString();
            UInt160 contract = UInt160.Parse(_params[1].AsString());
            string keyBase64 = _params[2].AsString();
            byte[] prefix = Convert.FromBase64String(keyBase64);

            FairyEngine oldEngine = sessionStringToFairySession[session].engine;
            ContractState contractState = NativeContract.ContractManagement.GetContract(oldEngine.Snapshot, contract);
            JObject json = new();
            foreach (var (key, value) in oldEngine.Snapshot.Find(StorageKey.CreateSearchPrefix(contractState.Id, prefix)))
                json[Convert.ToBase64String(key.Key.ToArray())] = Convert.ToBase64String(value.ToArray());
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
            FairyEngine oldEngine = sessionStringToFairySession[session].engine;
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
