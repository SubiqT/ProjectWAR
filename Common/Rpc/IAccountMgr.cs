using Common.Database.Account;
using FrameWork;
using System.Collections.Generic;

namespace Common
{
    /// <summary>
    /// Account service contract exposed by AccountCacher and consumed by the
    /// lobby, launcher and world servers over RPC.
    /// </summary>
    public interface IAccountMgr
    {
        void InitializeCache(bool enabled, int maxSize);

        Account LoadAccount(string username);

        Account GetAccount(string username);

        Account GetAccount(int accountId);

        Account GetAccountById(int? ID);

        void UpdateAccount(Account acct);

        IList<AccountSanctionInfo> GetSanctionsFor(int accountId);

        void AddSanction(AccountSanctionInfo sanct);

        LoginResult CheckAccount(string username, string password, string ip);

        LoginResult CheckAccount(string username, string password, string ip, out int accountId);

        bool CheckIp(string Ip);

        string GenerateToken(string username);

        AuthResult CheckToken(string Username, string Token);

        ResultCode CheckToken(string Token);

        void BanAccount(string Username, int Time);

        List<int> GetPendingAccounts();

        void LoadRealms();

        void LoadPending();

        bool AddPending(AccountPending Ap);

        bool AddRealm(Realm Rm);

        Realm GetRealm(byte RealmId);

        Realm GetRealmByRpc(int RpcId);

        List<Realm> GetRealms();

        int CheckCode(string username, string code);

        void UpdateRealmScenarioRotationTime(byte realmId, long nextRotation);

        bool UpdateRealm(RpcClientInfo Info, byte RealmId);

        void UpdateRealm(byte RealmId, uint OnlinePlayers, uint OrderCount, uint DestructionCount);

        void UpdateRealmCharacters(byte RealmId, uint OrderCharacters, uint DestruCharacters);

        byte[] BuildClusterList();

        bool CreateAccount(string username, string password, string email, int gmLevel, int langID, string ip = "127.0.0.1");

        void UpdateClientPatcherLog(int accountId, string log);

        void UpdateAccountBio(int accountId, string ip, string installID);

        string GetAccountSchemaName();
    }
}
