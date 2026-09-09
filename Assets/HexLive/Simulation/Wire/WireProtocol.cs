using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace HexLive.Simulation.Wire
{

/// <summary>
/// Frame kinds on the socket. Every message is one byte of kind followed by the
/// payload the matching codec wrote.
/// </summary>
public enum FrameKind : byte
{
    /// <summary>Server → client, once, on connect. See <see cref="Handshake"/>.</summary>
    Handshake = 1,

    /// <summary>
    /// Server → client: a complete <c>WorldSnapshotCodec</c> frame. Sent on
    /// connect, after a reconnect, and whenever the delta chain breaks.
    /// </summary>
    Snapshot = 2,

    /// <summary>Server → client: <c>SimulationEventCodec</c> payload.</summary>
    Events = 3,

    /// <summary>Client → server: pause / resume / speed.</summary>
    Command = 4,

    /// <summary>
    /// Client → server, echoed straight back as <see cref="Pong"/>. Carries an
    /// opaque token the client matches to work out round-trip time — and, more
    /// importantly, to notice that the connection has gone quiet: a socket that
    /// has died without a close frame looks exactly like a paused world until
    /// something asks it a question.
    /// </summary>
    Ping = 5,

    /// <summary>Server → client: the echo of a <see cref="Ping"/>.</summary>
    Pong = 6,

    /// <summary>
    /// Server → client: the world's clock changed (someone paused it, or moved
    /// the speed). Without this a viewer would have to guess pause from silence,
    /// and a paused world is indistinguishable from a broken connection.
    /// </summary>
    ServerClock = 7,

    /// <summary>
    /// Server → client: only what moved since the tick stamped inside. The
    /// receiver refuses one whose baseline is not the tick its mirror holds —
    /// applying a delta to the wrong state is exactly the silent corruption the
    /// whole design is arranged to avoid.
    /// </summary>
    SnapshotDelta = 8,

    /// <summary>
    /// Client → server: "my chain is broken, send me everything." The one
    /// recovery path, used after a gap, a decode failure or a reconnect.
    /// </summary>
    RequestKeyframe = 9,

    /// <summary>
    /// Client → server: приказ NPC (§121.9/§83) — int32 correlationId + команда
    /// в <see cref="SimulationCommandCodec"/>. Принимается только от
    /// авторизованного соединения (токен игрока в рукопожатии HTTP-upgrade);
    /// право на колонистку решает постоянное назначение §149, активный поток
    /// защищает реестр лиз, а правду о приказе — единственный валидатор
    /// ManualCommandExecutor.
    /// </summary>
    NpcCommand = 10,

    /// <summary>
    /// Server → client: синхронный вердикт границы приёма на ОДИН NpcCommand —
    /// correlationId + ManualCommandAdmission. Клиент по Rejected синтезирует
    /// локальное событие ManualOrderRejected, и существующий тост §121.5
    /// работает без единой правки UI. Отказы НЕ едут потоком событий нарочно:
    /// у потока нет адресата, и отказ одного игрока видели бы все зрители.
    /// </summary>
    CommandResult = 11,

    /// <summary>
    /// Server → client: §138 authoritative crafting read models for only the
    /// characters assigned to this viewer (§149). Replaces the previous
    /// remote-only hole where CraftItemCommand travelled but its UI options did
    /// not.
    /// </summary>
    CraftingOptions = 12,

    /// <summary>
    /// Server → client: другой кадр целиком (байт вида + payload), сжатый
    /// gzip'ом — §83.4. Шлётся ТОЛЬКО соединению, объявившему поддержку
    /// заголовком upgrade-запроса <c>X-HexLive-Accepts: gzip</c>, поэтому
    /// <see cref="Handshake.ProtocolVersion"/> не бампается: старый клиент
    /// просто продолжает получать несжатые кадры. Выгода — на больших кадрах
    /// (кейфрейм ~440 КБ ужимается в разы), и именно они душили медленный
    /// канал так, что понг не успевал к дедлайну живости клиента.
    /// </summary>
    Compressed = 13,

    /// <summary>§160 server → client: generic MCP attachment state.</summary>
    AgentState = 14,
    /// <summary>§160 client → server: request a short-lived Deepgram JWT.</summary>
    SttTokenRequest = 15,
    /// <summary>§160 server → client: Deepgram JWT or a bounded refusal.</summary>
    SttTokenResult = 16,
    /// <summary>§160 client → server: final recognized player text only.</summary>
    AgentTextInput = 17,
    /// <summary>§160 server → client: inbox admission verdict.</summary>
    AgentTextResult = 18,
    /// <summary>§160 server → client: metadata for chunked agent speech.</summary>
    AgentSpeechBegin = 19,
    /// <summary>§160 server → client: one raw WAV chunk.</summary>
    AgentSpeechChunk = 20,
    /// <summary>§160 server → client: utterance completion/checksum.</summary>
    AgentSpeechEnd = 21,
    AdminInput = 22,
    AdminResult = 23,
    AgentPairingInput = 24,
    AgentPairingResult = 25,
}

public enum CommandKind : byte
{
    Pause = 1,
    Resume = 2,
    SetSpeed = 3,
}

/// <summary>
/// What a client is told the moment it connects — everything it needs to build
/// its own copy of the static world before the first tick frame arrives.
/// <para>
/// The two interesting fields are <see cref="Seed"/> and <see cref="SimData"/>.
/// The seed lets the client regenerate the ~14 000 junctions locally instead of
/// receiving them (about 800 KB it never has to download), and the tuned catalog
/// travels WITH the world rather than being assumed to match: a client whose
/// ScriptableObjects had drifted would otherwise regenerate a subtly different
/// island and mis-render everything on it.
/// </para>
/// </summary>
public sealed class Handshake
{
    // 2: §21.21B v15 added HopFromTile to the NPC record.
    // 3: §121 added IsManualControl to the NPC record.
    // 4: simdata едет gzip'ом — 530 КБ текста против 35 КБ (15x).
    // 5: §121.9/§83 — кадры NpcCommand/CommandResult, поля ControlEnabled/
    //    ControlOwner в рукопожатии: игрок по сети управляет колонисткой.
    // 6: §146.2 — Mode: клиент регенерирует остров из (seed, mode), поэтому
    //    режим обязан ехать в рукопожатии, иначе topology checksum честно, но
    //    непонятно отвергал бы каждое подключение к BigIsland-миру.
    // 7: §147.5 — секция MobSlots в снапшоте/дельте (патрульные слоты
    //    виртуальных зверей; превью клиент считает сам из Seed+Tick).
    // 8: §133.9 — SetOutfitLockCommand и авторитетный OutfitLocked в NPC.
    // 9: §149.3 — AssignedNpcIds: постоянный ростер именно этого игрока.
    // 10: §138.2 — per-viewer FrameKind.CraftingOptions.
    // 11: snapshot v33 — owner ids of physical inventory/clothing items.
    // 12: §121.11/#294 — темп ручного приказа стал настройкой персонажа:
    //     MoveTo/GroupMove несут необязательный темп, появилась
    //     SetRunByDefault, а RunByDefault едет в записи NPC (snapshot v37).
    // 13: §160 generic MCP attachment, direct Deepgram STT and agent speech.
    // 16: §160/#361 — bounded relation view includes assessment reason and actual deltas.
    public const int ProtocolVersion = 16;

    public string WorldId { get; set; } = string.Empty;
    public string CreationConfig { get; set; } = string.Empty;

    public int Seed { get; set; }

    /// <summary>§146: ordinal of <c>GameMode</c> the server's world was
    /// created as. The client passes it to worldgen beside the seed.</summary>
    public int Mode { get; set; }

    public int Tick { get; set; }

    public float TickDeltaTime { get; set; }

    public float SpeedMultiplier { get; set; }

    public bool Paused { get; set; }

    /// <summary>Event seq the client should start asking from.</summary>
    public long EventSeq { get; set; }

    /// <summary>
    /// Checksum of the server's regenerated topology — tiles, plus junction ids,
    /// touching tiles and neighbours, all as integers (see
    /// <see cref="TopologyChecksum"/> for why no float may go in). The client
    /// recomputes it after its own worldgen and refuses to continue on a mismatch
    /// — that is the guard against the one soft spot in regenerating from a seed,
    /// a float rounding difference flipping a tile's elevation.
    /// </summary>
    public uint TopologyChecksum { get; set; }

    /// <summary>The server's <c>simdata.json</c>, verbatim.</summary>
    public string SimData { get; set; } = string.Empty;

    /// <summary>§121.9: сервер принял токен игрока — этому соединению можно
    /// слать <see cref="FrameKind.NpcCommand"/>. Ложь — прежний анонимный
    /// зритель: UI прячет весь ручной режим (SupportsNpcCommands).</summary>
    public bool ControlEnabled { get; set; }

    /// <summary>§121.9: канонический owner соединения в реестре лиз
    /// (например <c>ws:&lt;guid&gt;</c>) — для диагностики и сообщений
    /// «занято таким-то». Пустая строка у анонима.</summary>
    public string ControlOwner { get; set; } = string.Empty;

    /// <summary>§149: постоянные персонажи именно этого playerId. Это поле
    /// handshake, а не WorldSnapshot: у двух зрителей одного мира списки
    /// различаются.</summary>
    public List<int> AssignedNpcIds { get; } = new();

    public bool AgentIntegrationEnabled { get; set; }

    public bool SttAvailable { get; set; }

    public void Write(BinaryWriter w)
    {
        w.Write(ProtocolVersion);
        w.Write(Seed);
        w.Write(Mode);
        w.Write(Tick);
        w.Write(TickDeltaTime);
        w.Write(SpeedMultiplier);
        w.Write(Paused);
        w.Write(EventSeq);
        w.Write(TopologyChecksum);
        WriteSimData(w, SimData ?? string.Empty);
        w.Write(ControlEnabled);
        w.Write(ControlOwner ?? string.Empty);
        w.Write(AssignedNpcIds.Count);
        for (var i = 0; i < AssignedNpcIds.Count; i++)
        {
            w.Write(AssignedNpcIds[i]);
        }
        w.Write(AgentIntegrationEnabled);
        w.Write(SttAvailable);
        w.Write(WorldId ?? string.Empty);
        WriteSimData(w, CreationConfig ?? string.Empty);
    }

    public static Handshake Read(BinaryReader r)
    {
        var version = r.ReadInt32();
        if (version != ProtocolVersion)
        {
            throw new InvalidDataException(
                $"Handshake protocol version {version}, expected {ProtocolVersion}.");
        }

        var handshake = new Handshake
        {
            Seed = r.ReadInt32(),
            Mode = r.ReadInt32(),
            Tick = r.ReadInt32(),
            TickDeltaTime = r.ReadSingle(),
            SpeedMultiplier = r.ReadSingle(),
            Paused = r.ReadBoolean(),
            EventSeq = r.ReadInt64(),
            TopologyChecksum = r.ReadUInt32(),
            SimData = ReadSimData(r),
            ControlEnabled = r.ReadBoolean(),
            ControlOwner = r.ReadString(),
        };

        var assignedCount = r.ReadInt32();
        if (assignedCount < 0 || assignedCount > 1024)
        {
            throw new InvalidDataException(
                $"Handshake claims {assignedCount} assigned NPCs — not plausible.");
        }

        for (var i = 0; i < assignedCount; i++)
        {
            handshake.AssignedNpcIds.Add(r.ReadInt32());
        }

        handshake.AgentIntegrationEnabled = r.ReadBoolean();
        handshake.SttAvailable = r.ReadBoolean();
        handshake.WorldId = r.ReadString();
        handshake.CreationConfig = ReadSimData(r);

        return handshake;
    }

    /// <summary>
    /// Экспортированные каталоги — самый крупный кусок, который вообще
    /// пересекает провод: 530 КБ отформатированного JSON, и это ОДИН раз на
    /// подключение против ~4.6 КБ/с потока. Он же и самый сжимаемый: имена
    /// ручек повторяются тысячами, gzip даёт 35 КБ, пятнадцатикратно.
    /// <para>
    /// Жмётся здесь, а не «где-нибудь по дороге», по той же причине, по которой
    /// весь провод живёт в сборке симуляции: тогда сжатие и разжатие — это
    /// одно место и один формат, а не договорённость между сервером и клиентом,
    /// которую можно разойтись.
    /// </para>
    /// </summary>
    private static void WriteSimData(BinaryWriter w, string json)
    {
        var raw = Encoding.UTF8.GetBytes(json);
        byte[] packed;
        using (var packedStream = new MemoryStream())
        {
            using (var gzip = new GZipStream(packedStream, CompressionLevel.Optimal, true))
            {
                gzip.Write(raw, 0, raw.Length);
            }

            packed = packedStream.ToArray();
        }

        w.Write(raw.Length);
        w.Write(packed.Length);
        w.Write(packed);
    }

    private static string ReadSimData(BinaryReader r)
    {
        var rawLength = r.ReadInt32();
        var packedLength = r.ReadInt32();

        // Длина разжатого объявлена отправителем, поэтому ей нельзя верить на
        // слово: без потолка «сервер» из одного кадра просит гигабайт памяти.
        // 64 МБ — с большим запасом над реальными 530 КБ.
        if (rawLength < 0 || rawLength > 64 * 1024 * 1024 || packedLength < 0 || packedLength > rawLength + 1024)
        {
            throw new InvalidDataException(
                $"Handshake simdata block claims {rawLength} bytes packed into {packedLength} — not plausible.");
        }

        var packed = r.ReadBytes(packedLength);
        var raw = new byte[rawLength];
        using (var packedStream = new MemoryStream(packed))
        using (var gzip = new GZipStream(packedStream, CompressionMode.Decompress))
        {
            var read = 0;
            while (read < rawLength)
            {
                var got = gzip.Read(raw, read, rawLength - read);
                if (got <= 0)
                {
                    throw new InvalidDataException(
                        $"Handshake simdata ended after {read} of {rawLength} bytes.");
                }

                read += got;
            }

            if (gzip.ReadByte() >= 0)
            {
                throw new InvalidDataException(
                    $"Compressed frame expands beyond its declared {rawLength} bytes.");
            }
        }

        return Encoding.UTF8.GetString(raw);
    }
}

/// <summary>Framing helpers shared by both ends.</summary>
public static class Frame
{
    public static byte[] Wrap(FrameKind kind, byte[] payload)
    {
        var framed = new byte[payload.Length + 1];
        framed[0] = (byte)kind;
        Buffer.BlockCopy(payload, 0, framed, 1, payload.Length);
        return framed;
    }

    public static byte[] Handshake(Handshake handshake)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        handshake.Write(writer);
        writer.Flush();
        return Wrap(FrameKind.Handshake, stream.ToArray());
    }

    /// <summary>
    /// §83.4: целый кадр (байт вида + payload) → кадр <see cref="FrameKind.Compressed"/>.
    /// Layout payload'а повторяет блок simdata: int32 длина сырого, int32 длина
    /// сжатого, gzip-байты — то же одно место и один формат на оба конца.
    /// </summary>
    public static byte[] Compress(byte[] frame)
    {
        byte[] packed;
        using (var packedStream = new MemoryStream())
        {
            using (var gzip = new GZipStream(packedStream, CompressionLevel.Fastest, true))
            {
                gzip.Write(frame, 0, frame.Length);
            }

            packed = packedStream.ToArray();
        }

        // A handshake already contains gzip-compressed simdata. Depending on
        // the concrete bytes, a second Fastest pass can grow it (the production
        // handshake observed on 2026-08-27 grew 34 204 -> 35 817 bytes). Sending
        // a larger transport envelope wastes bandwidth and, more importantly,
        // used to trip the receiver's old "raw + 1024" plausibility guard so
        // the client discarded its first Handshake and stayed Connecting
        // forever. Compression is an optimization: when it does not pay for its
        // own kind/length header, send the original frame byte-for-byte.
        if (packed.Length + 9 >= frame.Length)
        {
            return frame;
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(frame.Length);
        writer.Write(packed.Length);
        writer.Write(packed);
        writer.Flush();
        return Wrap(FrameKind.Compressed, stream.ToArray());
    }

    /// <summary>Обратно: payload кадра Compressed → внутренний кадр целиком.</summary>
    public static byte[] Decompress(byte[] payload)
    {
        using var stream = new MemoryStream(payload);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        var rawLength = reader.ReadInt32();
        var packedLength = reader.ReadInt32();

        // Той же меркой, что simdata: длинам нельзя верить на слово. Но
        // packed не обязан быть меньше raw: старый prod уже успел выдать
        // валидный, но расширившийся gzip-handshake. Клиент обязан его принять;
        // новый сервер выше просто пошлёт такой кадр несжатым. Оба
        // буфера и точное тело по-прежнему ограничены 64 MiB.
        if (rawLength < 1 || rawLength > 64 * 1024 * 1024 ||
            packedLength < 1 || packedLength > 64 * 1024 * 1024 ||
            packedLength != payload.Length - 8)
        {
            throw new InvalidDataException(
                $"Compressed frame claims {rawLength} bytes packed into {packedLength} — not plausible.");
        }

        var packed = reader.ReadBytes(packedLength);
        var raw = new byte[rawLength];
        using (var packedStream = new MemoryStream(packed))
        using (var gzip = new GZipStream(packedStream, CompressionMode.Decompress))
        {
            var read = 0;
            while (read < rawLength)
            {
                var got = gzip.Read(raw, read, rawLength - read);
                if (got <= 0)
                {
                    throw new InvalidDataException(
                        $"Compressed frame ended after {read} of {rawLength} bytes.");
                }

                read += got;
            }
        }

        return raw;
    }

    public static byte[] Command(CommandKind kind, float value)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write((byte)kind);
        writer.Write(value);
        writer.Flush();
        return Wrap(FrameKind.Command, stream.ToArray());
    }

    public static byte[] Ping(long token) => WithLong(FrameKind.Ping, token);

    public static byte[] Pong(long token) => WithLong(FrameKind.Pong, token);

    public static byte[] ServerClock(bool paused, float speedMultiplier)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(paused);
        writer.Write(speedMultiplier);
        writer.Flush();
        return Wrap(FrameKind.ServerClock, stream.ToArray());
    }

    public static (bool paused, float speedMultiplier) ReadServerClock(byte[] payload)
    {
        using var stream = new MemoryStream(payload);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        return (reader.ReadBoolean(), reader.ReadSingle());
    }

    public static long ReadLong(byte[] payload)
    {
        using var stream = new MemoryStream(payload);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        return reader.ReadInt64();
    }

    // ── §121.9: приказы NPC и вердикты ───────────────────────────────────

    public static byte[] NpcCommand(int correlationId, Runtime.ISimulationCommand command)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(correlationId);
        SimulationCommandCodec.Write(writer, command);
        writer.Flush();
        return Wrap(FrameKind.NpcCommand, stream.ToArray());
    }

    public static (int CorrelationId, Runtime.ISimulationCommand Command) ReadNpcCommand(byte[] payload)
    {
        using var stream = new MemoryStream(payload);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        var correlationId = reader.ReadInt32();
        var command = SimulationCommandCodec.Read(reader);
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException(
                $"NpcCommand frame carries {stream.Length - stream.Position} " +
                "trailing bytes — codec and frame disagree.");
        }

        return (correlationId, command);
    }

    /// <summary>Вердикт границы приёма для одного NpcCommand.</summary>
    public readonly struct CommandResultFrame
    {
        public CommandResultFrame(
            int correlationId, bool accepted, int? actorId, string order, string reason)
        {
            CorrelationId = correlationId;
            Accepted = accepted;
            ActorId = actorId;
            Order = order ?? string.Empty;
            Reason = reason ?? string.Empty;
        }

        public int CorrelationId { get; }
        public bool Accepted { get; }
        public int? ActorId { get; }
        public string Order { get; }
        public string Reason { get; }
    }

    public static byte[] CommandResult(int correlationId, Runtime.ManualCommandAdmission admission)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(correlationId);
        writer.Write(admission.Accepted);
        WireIo.WriteNullableInt(writer, admission.Actor?.Value);
        writer.Write(admission.Order ?? string.Empty);
        writer.Write(admission.Reason ?? string.Empty);
        writer.Flush();
        return Wrap(FrameKind.CommandResult, stream.ToArray());
    }

    /// <summary>Вердикт с ГОТОВОЙ причиной — для отказов самого сервера
    /// (нет лиза, rate-limit, аноним), которые до симуляции не дошли.</summary>
    public static byte[] CommandResult(
        int correlationId, bool accepted, int? actorId, string order, string reason)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(correlationId);
        writer.Write(accepted);
        WireIo.WriteNullableInt(writer, actorId);
        writer.Write(order ?? string.Empty);
        writer.Write(reason ?? string.Empty);
        writer.Flush();
        return Wrap(FrameKind.CommandResult, stream.ToArray());
    }

    public static CommandResultFrame ReadCommandResult(byte[] payload)
    {
        using var stream = new MemoryStream(payload);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        return new CommandResultFrame(
            reader.ReadInt32(),
            reader.ReadBoolean(),
            WireIo.ReadNullableInt(reader),
            reader.ReadString(),
            reader.ReadString());
    }

    // ── §138: authoritative remote crafting read model ──────────────────

    public static byte[] CraftingOptions(CraftingOptionsSnapshot snapshot)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        CraftingOptionsCodec.Write(snapshot, writer);
        writer.Flush();
        return Wrap(FrameKind.CraftingOptions, stream.ToArray());
    }

    public static CraftingOptionsSnapshot ReadCraftingOptions(byte[] payload)
    {
        using var stream = new MemoryStream(payload);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        return CraftingOptionsCodec.Read(reader);
    }

    private static byte[] WithLong(FrameKind kind, long value)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(value);
        writer.Flush();
        return Wrap(kind, stream.ToArray());
    }
}

}
