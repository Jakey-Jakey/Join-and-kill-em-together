namespace Jaket.Net.Endpoints;

using Connection = Jaket.Net.Backends.INetConnection;

using Jaket.Content;
using Jaket.IO;
using Jaket.Net.Admin;
using Jaket.Net.Backends;
using Jaket.Net.Types;
using Jaket.Sprays;
using Jaket.UI.Elements;
using Jaket.World;

/// <summary> Endpoint of a client connection that processes socket events and server data. </summary>
public class Client : Endpoint, INetListener
{
    static Pools ents => Networking.Entities;

    /// <summary> Networking sockets backend. </summary>
    public INetClient Manager { get; private set; }

    public override void Create()
    {
        Listen(PacketType.Level, World.ReadData);

        Listen(PacketType.Snapshot, r =>
        {
            var id = r.Id();
            var type = r.EntityType();

            if (ents.TryGetValue(id, out var entity)) entity.Read(r);
            else
            {
                entity = Entities.Supply(id, type);

                entity.Read(r);
                entity.Create();
                entity.Push();
            }
        });

        Listen(PacketType.Hitscan, r => Entities.Hitscans.Make(r.EntityType(), r.Vector(), r.Vector(), r.Bool(), r.Byte()));

        Listen(PacketType.Damage, r =>
        {
            if (ents.TryGetValue(r.Id(), out var e)) e.Damage(r);
        });

        Listen(PacketType.Death, (con, sender, r, s) =>
        {
            if (ents.TryGetValue(r.Id(), out var e)) e.Killed(r, s - 5);
        });

        Listen(PacketType.Style, r =>
        {
            if (ents[r.Id()] is RemotePlayer p) p.Doll.ReadSuit(r);
        });

        Listen(PacketType.Punch, r =>
        {
            if (ents[r.Id()] is RemotePlayer p) p.Punch(r);
        });

        Listen(PacketType.Point, r =>
        {
            if (ents[r.Id()] is RemotePlayer p)
            {
                if (p.Point) p.Point.Lifetime = 5.5f;
                p.Point = Point.Spawn(r.Vector(), r.Vector(), p.Team, p);
            }
        });

        Listen(PacketType.Spray, r =>
        {
            if (ents[r.Id()] is RemotePlayer p)
            {
                if (p.Spray) p.Spray.Lifetime = 58f;
                p.Spray = Spray.Spawn(r.Vector(), r.Vector(), p.Team, p);
            }
        });

        Listen(PacketType.ImageHeader, r =>
        {
            SprayDistributor.Download(r.Id(), r.Int());
        });

        Listen(PacketType.ImageChunk, (con, sender, r, s) =>
        {
            SprayDistributor.ProcessDownload(r.Id(), s - 5, r);
        });

        Listen(PacketType.WorldAction, r => World.Perform(r.Byte(), new(r.Float(), r.Float())));

        Listen(PacketType.Chat, (con, sender, r, s) => ChatChannel.Receive(r.Id(), s - 5, r));

        /*
        Listen(PacketType.Vote, r => Votes.UpdateVote(r.Id(), r.Byte()));
        */
    }

    public override void Update()
    {
        Stats.MeasureTime(ref Stats.ReadMs, () => Manager.Receive());
        Stats.MeasureTime(ref Stats.WriteMs, () =>
        {
            if (Networking.Loading) return;
            ents.ClientPool(pool = ++pool % 3, 3, e => Networking.Send(PacketType.Snapshot, e.BufferSize, w =>
            {
                w.Id(e.Id);
                w.Enum(e.Type);
                e.Write(w);
            }));
        });
        Stats.MeasureTime(ref Stats.FlushMs, () =>
        {
            Manager.Connection.Flush();
            Pointers.Free();
        });
    }

    public override void Close()
    {
        Manager?.Close();
        Manager = null;
    }

    public void Connect() => Manager = NetBackends.Current.Connect(this);

    #region manager

    public void OnConnecting(Connection con, uint accId, bool authentic) => Log.Info("[CLIENT] Connecting...");

    public void OnConnected(Connection con, uint accId) => Log.Info("[CLIENT] Connected");

    public void OnDisconnected(Connection con, uint accId) => Log.Info("[CLIENT] Disconnected");

    public void OnMessage(Connection con, uint sender, Ptr data, int size) => Handle(con, sender, data, size);

    #endregion
}
