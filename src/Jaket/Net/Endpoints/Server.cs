namespace Jaket.Net.Endpoints;

using Steamworks;
using Steamworks.Data;

using Jaket.Content;
using Jaket.IO;
using Jaket.Net.Types;
using Jaket.Sprays;
using Jaket.World;

/// <summary> Host endpoint processing socket events and client packets. </summary>
public class Server : Endpoint, ISocketManager
{
    /// <summary> Steam networking sockets API backend. </summary>
    public SocketManager Manager { get; protected set; }

    public override void Load()
    {
        Listen(PacketType.Snapshot, (con, sender, r) =>
        {
            var id = r.Id();
            var type = r.Enum<EntityType>();

            // player can only have one doll and its id should match the player's id
            if ((id == sender && type != EntityType.Player) || (id != sender && type == EntityType.Player)) return;

            if (!ents.ContainsKey(id) || ents[id] == null)
            {
                // double-check on cheats just in case of any custom multiplayer clients existence
                if (!LobbyController.CheatsAllowed && (type.IsEnemy() || type.IsItem())) return;

                // client cannot create special enemies
                if (type.IsEnemy() && !type.IsCommonEnemy()) return;

                Administration.Handle(sender, ents[id] = Entities.Get(id, type));
            }
            ents[id]?.Read(r);
        });

        Listen(PacketType.SpawnEntity, (con, sender, r) =>
        {
            var type = r.Enum<EntityType>();
            if (type.IsBullet() && Administration.CanSpawnEntityBullet(sender))
            {
                var bullet = Bullets.EInstantiate(type);

                bullet.transform.position = r.Vector();
                bullet.transform.eulerAngles = r.Vector();
                bullet.InitSpeed = r.Float();

                bullet.Owner = sender.AccountId;
                bullet.OnTransferred();
                Administration.EntityBullets[sender].Add(bullet);
            }
            else if (type.IsEnemy() && LobbyController.CheatsAllowed)
            {
                var enemy = Enemies.Instantiate(type);
                enemy.transform.position = r.Vector();

                Administration.EnemySpawned(sender, enemy, type.IsBigEnemy());
            }
            else if (type.IsPlushy())
            {
                var plushy = Items.Instantiate(type);
                plushy.transform.position = r.Vector();

                Administration.PlushySpawned(sender, plushy);
            }
        });

        Listen(PacketType.SpawnBullet, (con, sender, r) =>
        {
            var type = r.Byte(); r.Position = 1; // extract the bullet type
            int cost = type == 4 ? 2 : type >= 17 && type <= 19 ? 8 : 1; // coin - 2, rail - 8, other - default

            if (Administration.CanSpawnCommonBullet(sender, cost))
            {
                Bullets.CInstantiate(r);
                Redirect(r, con);
            }
        });

        ListenAndRedirect(PacketType.DamageEntity, r => entities[r.Id()]?.Damage(r));

        Listen(PacketType.KillEntity, (con, sender, r) =>
        {
            var entity = entities[r.Id()];
            if (entity && entity is Bullet bullet && bullet.Owner == sender)
            {
                bullet.Kill();
                Redirect(r, con);
            }
        });

        ListenAndRedirect(PacketType.Style, r =>
        {
            if (entities[r.Id()] is RemotePlayer player) player?.Doll.ReadSuit(r);
        });
        ListenAndRedirect(PacketType.Punch, r =>
        {
            if (entities[r.Id()] is RemotePlayer player) player?.Punch(r);
        });
        ListenAndRedirect(PacketType.Point, r =>
        {
            if (entities[r.Id()] is RemotePlayer player) player?.Point(r);
        });

        ListenAndRedirect(PacketType.Spray, r => SprayManager.Spawn(r.Id(), r.Vector(), r.Vector()));

        Listen(PacketType.ImageChunk, (con, sender, r) =>
        {
            var owner = r.Id(); r.Position = 1; // extract the spray owner

            // stop an attempt to overwrite someone else's spray, because this can lead to tragic consequences
            if (sender != owner)
            {
                Administration.Ban(sender);
                Log.Warning($"{sender} was blocked due to an attempt to overwrite someone else's spray");
            }
            else
            {
                SprayDistributor.Download(r);
                Redirect(r, con);
            }
        });

        Listen(PacketType.RequestImage, (con, sender, r) =>
        {
            var owner = r.Id();
            if (SprayDistributor.Requests.TryGetValue(owner, out var list)) list.Add(con);
            else
            {
                list = new();
                list.Add(con);
                SprayDistributor.Requests.Add(owner, list);
            }

            Log.Debug($"[Server] Got an image request for spray#{owner}. Count: {list.Count}");
        });

        Listen(PacketType.ActivateObject, r => World.Instance.ReadAction(r));
    }

    public override void Update()
    {
        Stats.MeasureTime(() => Manager.Receive(1024), () =>
        {
            Networking.EachEntity(entity => Networking.Send(PacketType.Snapshot, w =>
            {
                w.Id(entity.Id);
                w.Enum(entity.Type);
                entity.Write(w);
            }));
        });

        // flush data
        foreach (var con in Manager.Connected) con.Flush();
        Pointers.Free();
    }

    public override void Close() => Manager?.Close();

    public void Open()
    {
        Manager = SteamNetworkingSockets.CreateRelaySocket<SocketManager>(4242);
        Manager.Interface = this;
    }

    #region manager

    public void OnConnecting(Connection con, ConnectionInfo info)
    {
        Log.Info("[Server] Someone is connecting...");
        var identity = info.Identity;
        var accId = identity.SteamId.AccountId;

        // multiple connections are prohibited
        if (identity.IsSteamId && Networking.FindCon(accId).HasValue)
        {
            Log.Debug("[Server] Connection is rejected: already connected");
            con.Close();
            return;
        }

        // check if the player is banned
        if (identity.IsSteamId && Administration.Banned.Contains(accId))
        {
            Log.Debug("[Server] Connection is rejected: banned");
            con.Close();
            return;
        }

        // this will be used later to find the connection by the id
        con.ConnectionName = accId.ToString();

        // only steam users in the lobby can connect to the server
        if (identity.IsSteamId && LobbyController.Contains(accId))
            con.Accept();
        else
        {
            Log.Debug("[Server] Connection rejected: either a non-steam user or not in the lobby");
            con.Close();
        }
    }

    public void OnConnected(Connection con, ConnectionInfo info)
    {
        Log.Info($"[Server] {info.Identity.SteamId} connected");
        Networking.Send(PacketType.LoadLevel, World.Instance.WriteData, (data, size) => Tools.Send(con, data, size));
    }

    public void OnDisconnected(Connection con, ConnectionInfo info) => Log.Info($"[Server] {info.Identity.SteamId} disconnected");

    public void OnMessage(Connection con, NetIdentity id, System.IntPtr data, int size, long msg, long time, int channel) => Handle(con, id.SteamId.AccountId, data, size);

    #endregion
}
