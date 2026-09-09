using System.Globalization;
using IndustrialCommunication.Configuration;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace IndustrialCommunication.OpcUa;

/// <summary>
/// OPC UA driver built on the official OPCFoundation.NetStandard stack (1.5.378). Addresses are
/// NodeId strings, e.g. "ns=2;s=PumpSpeed" or "i=2259". Reconnection follows the unified model:
/// a failing operation (or the connection-monitor heartbeat on the ServerStatus node) marks the
/// session down and the next operation creates a fresh one.
/// </summary>
public sealed partial class OpcUaPlcClient : PlcClientBase
{
    private readonly OpcUaOptions _options;
    private readonly ITelemetryContext _telemetry;
    private readonly ILogger _log;
    private ApplicationConfiguration? _applicationConfiguration;
    private ConfiguredEndpoint? _endpoint;
    private ISession? _session;

    public OpcUaPlcClient(OpcUaOptions options, DeviceConfig device, ClientBuildContext context)
        : base(device, context.Runtime, options.DataLayout, options.ResolveEncoding(),
            context.LoggerFactory.CreateLogger(device.Name))
    {
        _options = options;
        _telemetry = DefaultTelemetry.Create(_ => { });
        _log = Logger;
    }

    protected override async Task DoConnectAsync(CancellationToken ct)
    {
        var configuration = _applicationConfiguration ??= await BuildConfigurationAsync(ct).ConfigureAwait(false);
        var endpoint = _endpoint ??= await SelectEndpointAsync(configuration, ct).ConfigureAwait(false);

        IUserIdentity identity = string.IsNullOrEmpty(_options.Username)
            ? new UserIdentity()
            : new UserIdentity(_options.Username, System.Text.Encoding.UTF8.GetBytes(_options.Password ?? string.Empty));

        var session = await new DefaultSessionFactory(_telemetry).CreateAsync(
            configuration,
            endpoint,
            updateBeforeConnect: false,
            checkDomain: false,
            sessionName: $"{Device.Name} ({_options.EndpointUrl})",
            sessionTimeout: (uint)_options.SessionTimeoutMs,
            identity,
            preferredLocales: null,
            ct).ConfigureAwait(false);

        session.KeepAliveInterval = _options.KeepAliveIntervalMs;
        session.KeepAlive += OnKeepAlive;

        _session = session;
    }

    protected override async Task DoDisconnectAsync()
    {
        var session = _session;
        _session = null;
        if (session is null)
            return;

        try
        {
            await session.CloseAsync(closeChannel: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogCloseError(Device.Name, ex);
        }
        finally
        {
            session.Dispose();
        }
    }

    protected override DeviceAddress ParseAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new FormatException("OPC UA address must not be empty.");

        try
        {
            NodeId.Parse(address);
        }
        catch (Exception ex)
        {
            throw new FormatException($"'{address}' is not a valid NodeId ({ex.Message}).");
        }

        return new DeviceAddress { Area = "NODE", Offset = 0 };
    }

    private static NodeId ParseNodeId(DeviceAddress address, string original)
    {
        try
        {
            return NodeId.Parse(original);
        }
        catch (Exception ex)
        {
            throw new FormatException($"'{original}' is not a valid NodeId ({ex.Message}).");
        }
    }

    protected override async Task<CommResult<bool[]>> DoReadBitsAsync(DeviceAddress address, ushort count, CancellationToken ct)
    {
        var session = RequireSession();
        try
        {
            var value = await session.ReadValueAsync(GetNodeId(address), ct).ConfigureAwait(false);
            if (StatusCode.IsBad(value.StatusCode))
                return CommResult<bool[]>.Fail(CommErrorKind.DeviceRejected,
                    value.StatusCode.Code.ToString(CultureInfo.InvariantCulture), "The server reported a bad status code.");

            var bits = OpcUaValue.ToBits(value.Value, count);
            return bits.Success
                ? bits
                : CommResult<bool[]>.Fail(bits.Kind, bits.ErrorCode, bits.Message);
        }
        catch (ServiceResultException ex)
        {
            return CommResult<bool[]>.Fail(MapServiceResult(ex), ex.StatusCode.ToString(CultureInfo.InvariantCulture), ex.Message);
        }
    }

    protected override async Task<CommResult> DoWriteBitsAsync(DeviceAddress address, IReadOnlyList<bool> values, CancellationToken ct)
    {
        var session = RequireSession();
        try
        {
            var nodeValue = values.Count == 1 ? (object)values[0] : values.ToArray();
            var response = await session.WriteAsync(null,
                [new WriteValue { NodeId = GetNodeId(address), AttributeId = Attributes.Value, Value = new DataValue { Value = nodeValue } }],
                ct).ConfigureAwait(false);

            var bad = response.Results.FirstOrDefault(StatusCode.IsBad);
            return bad == default
                ? CommResult.Ok()
                : CommResult.Fail(CommErrorKind.DeviceRejected, bad.Code.ToString(CultureInfo.InvariantCulture),
                    $"The server rejected the write (status 0x{bad:X8}).");
        }
        catch (ServiceResultException ex)
        {
            return CommResult.Fail(MapServiceResult(ex), ex.StatusCode.ToString(CultureInfo.InvariantCulture), ex.Message);
        }
    }

    protected override async Task<CommResult<ushort[]>> DoReadWordsAsync(DeviceAddress address, ushort count, CancellationToken ct)
    {
        var session = RequireSession();
        try
        {
            var value = await session.ReadValueAsync(GetNodeId(address), ct).ConfigureAwait(false);
            if (StatusCode.IsBad(value.StatusCode))
                return CommResult<ushort[]>.Fail(CommErrorKind.DeviceRejected,
                    value.StatusCode.Code.ToString(CultureInfo.InvariantCulture), "The server reported a bad status code.");

            var words = OpcUaValue.ToWords(value.Value, count);
            return words.Success
                ? words
                : CommResult<ushort[]>.Fail(words.Kind, words.ErrorCode, words.Message);
        }
        catch (ServiceResultException ex)
        {
            return CommResult<ushort[]>.Fail(MapServiceResult(ex), ex.StatusCode.ToString(CultureInfo.InvariantCulture), ex.Message);
        }
    }

    protected override async Task<CommResult> DoWriteWordsAsync(DeviceAddress address, IReadOnlyList<ushort> values, CancellationToken ct)
    {
        var session = RequireSession();
        try
        {
            var nodeValue = values.Count == 1 ? (object)values[0] : values.ToArray();
            var response = await session.WriteAsync(null,
                [new WriteValue { NodeId = GetNodeId(address), AttributeId = Attributes.Value, Value = new DataValue { Value = nodeValue } }],
                ct).ConfigureAwait(false);

            var bad = response.Results.FirstOrDefault(StatusCode.IsBad);
            return bad == default
                ? CommResult.Ok()
                : CommResult.Fail(CommErrorKind.DeviceRejected, bad.Code.ToString(CultureInfo.InvariantCulture),
                    $"The server rejected the write (status 0x{bad:X8}).");
        }
        catch (ServiceResultException ex)
        {
            return CommResult.Fail(MapServiceResult(ex), ex.StatusCode.ToString(CultureInfo.InvariantCulture), ex.Message);
        }
    }

    public override async Task<CommResult<T>> ReadAsync<T>(string address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        ValueTypeMap.FromClrType(typeof(T));

        return await ExecuteAsync(async token =>
        {
            var session = RequireSession();
            try
            {
                var value = await session.ReadValueAsync(ParseNodeId(ParseAddress(address), address), token).ConfigureAwait(false);
                if (StatusCode.IsBad(value.StatusCode))
                    return CommResult<T>.Fail(CommErrorKind.DeviceRejected,
                        value.StatusCode.Code.ToString(CultureInfo.InvariantCulture), "The server reported a bad status code.");

                if (!OpcUaValue.TryConvert<T>(value.Value, out var converted))
                    return CommResult<T>.Fail(CommErrorKind.InvalidArgument, null,
                        $"Node value '{value.Value?.GetType().Name ?? "null"}' cannot be converted to {typeof(T).Name}.");

                return CommResult<T>.Ok(converted);
            }
            catch (ServiceResultException ex)
            {
                return CommResult<T>.Fail(MapServiceResult(ex), ex.StatusCode.ToString(CultureInfo.InvariantCulture), ex.Message);
            }
        }, ct).ConfigureAwait(false);
    }

    public override async Task<CommResult> WriteAsync<T>(string address, T value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        ValueTypeMap.FromClrType(typeof(T));

        return await ExecuteAsync(async token =>
        {
            var session = RequireSession();
            try
            {
                var response = await session.WriteAsync(null,
                    [new WriteValue { NodeId = ParseNodeId(ParseAddress(address), address), AttributeId = Attributes.Value, Value = new DataValue { Value = value } }],
                    token).ConfigureAwait(false);

                var bad = response.Results.FirstOrDefault(StatusCode.IsBad);
                return bad == default
                    ? CommResult.Ok()
                    : CommResult.Fail(CommErrorKind.DeviceRejected, bad.Code.ToString(CultureInfo.InvariantCulture),
                        $"The server rejected the write (status 0x{bad:X8}). OPC UA servers enforce the node's native type.");
            }
            catch (ServiceResultException ex)
            {
                return CommResult.Fail(MapServiceResult(ex), ex.StatusCode.ToString(CultureInfo.InvariantCulture), ex.Message);
            }
        }, ct).ConfigureAwait(false);
    }

    public override async Task<CommResult<string>> ReadStringAsync(string address, ushort wordLength, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        return await ExecuteAsync(async token =>
        {
            var session = RequireSession();
            try
            {
                var value = await session.ReadValueAsync(ParseNodeId(ParseAddress(address), address), token).ConfigureAwait(false);
                if (StatusCode.IsBad(value.StatusCode))
                    return CommResult<string>.Fail(CommErrorKind.DeviceRejected,
                        value.StatusCode.Code.ToString(CultureInfo.InvariantCulture), "The server reported a bad status code.");

                return CommResult<string>.Ok(
                    value.Value is string text ? text : Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? string.Empty);
            }
            catch (ServiceResultException ex)
            {
                return CommResult<string>.Fail(MapServiceResult(ex), ex.StatusCode.ToString(CultureInfo.InvariantCulture), ex.Message);
            }
        }, ct).ConfigureAwait(false);
    }

    public override async Task<CommResult> WriteStringAsync(string address, string value, ushort wordLength, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(value);

        return await ExecuteAsync(async token =>
        {
            var session = RequireSession();
            try
            {
                var response = await session.WriteAsync(null,
                    [new WriteValue { NodeId = ParseNodeId(ParseAddress(address), address), AttributeId = Attributes.Value, Value = new DataValue { Value = value } }],
                    token).ConfigureAwait(false);

                var bad = response.Results.FirstOrDefault(StatusCode.IsBad);
                return bad == default
                    ? CommResult.Ok()
                    : CommResult.Fail(CommErrorKind.DeviceRejected, bad.Code.ToString(CultureInfo.InvariantCulture),
                        $"The server rejected the write (status 0x{bad:X8}).");
            }
            catch (ServiceResultException ex)
            {
                return CommResult.Fail(MapServiceResult(ex), ex.StatusCode.ToString(CultureInfo.InvariantCulture), ex.Message);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Batch read: one UA Read service call for the whole group; per-point statuses from the service results.</summary>
    public override async Task<GroupReadResult> ReadGroupAsync(IReadOnlyList<DevicePoint> points, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(points);

        var values = new Dictionary<string, object?>(points.Count, StringComparer.Ordinal);
        var statuses = new Dictionary<string, CommResult>(points.Count, StringComparer.Ordinal);

        var parsed = new List<(DevicePoint Point, NodeId Node)>();
        foreach (var point in points)
        {
            try
            {
                parsed.Add((point, NodeId.Parse(point.Address)));
            }
            catch (Exception ex)
            {
                statuses[point.Name] = CommResult.Fail(CommErrorKind.InvalidAddress, null, $"'{point.Address}' is not a valid NodeId ({ex.Message}).");
                values[point.Name] = null;
            }
        }

        if (parsed.Count == 0)
            return new GroupReadResult(values, statuses);

        var status = await ExecuteAsync<object?>(async token =>
        {
            var session = RequireSession();
            var (dataValues, errors) = await session.ReadValuesAsync([.. parsed.Select(p => p.Node)], token).ConfigureAwait(false);
            for (int i = 0; i < parsed.Count; i++)
            {
                var point = parsed[i].Point;
                if (ServiceResult.IsBad(errors[i]) || StatusCode.IsBad(dataValues[i].StatusCode))
                {
                    statuses[point.Name] = CommResult.Fail(CommErrorKind.DeviceRejected,
                        dataValues[i].StatusCode.Code.ToString(CultureInfo.InvariantCulture),
                        $"The server reported a bad status code for '{point.Address}'.");
                    values[point.Name] = null;
                    continue;
                }

                var raw = dataValues[i].Value;
                values[point.Name] = point.ValueType switch
                {
                    PlcValueType.Bit => OpcUaValue.TryConvert<bool>(raw, out var b) ? b : raw,
                    PlcValueType.Int16 => OpcUaValue.TryConvert<short>(raw, out var s) ? s : raw,
                    PlcValueType.UInt16 => OpcUaValue.TryConvert<ushort>(raw, out var us) ? us : raw,
                    PlcValueType.Int32 => OpcUaValue.TryConvert<int>(raw, out var i32) ? i32 : raw,
                    PlcValueType.UInt32 => OpcUaValue.TryConvert<uint>(raw, out var u32) ? u32 : raw,
                    PlcValueType.Int64 => OpcUaValue.TryConvert<long>(raw, out var i64) ? i64 : raw,
                    PlcValueType.Float32 => OpcUaValue.TryConvert<float>(raw, out var f) ? f : raw,
                    PlcValueType.Float64 => OpcUaValue.TryConvert<double>(raw, out var d) ? d : raw,
                    PlcValueType.String => Convert.ToString(raw, CultureInfo.InvariantCulture),
                    _ => raw,
                };
                statuses[point.Name] = CommResult.Ok();
            }

            return CommResult<object?>.Ok(null);
        }, ct).ConfigureAwait(false);

        if (!status.Success)
        {
            foreach (var (point, _) in parsed)
            {
                if (!statuses.ContainsKey(point.Name))
                {
                    statuses[point.Name] = CommResult.Fail(status.Kind, status.ErrorCode, status.Message);
                    values[point.Name] = null;
                }
            }
        }

        return new GroupReadResult(values, statuses);
    }

    protected override async Task<CommResult> DoHeartbeatAsync(CancellationToken ct)
    {
        var session = RequireSession();
        try
        {
            // Server_ServerStatus_State (i=2259) exists on every compliant server.
            var value = await session.ReadValueAsync(NodeId.Parse("i=2259"), ct).ConfigureAwait(false);
            return StatusCode.IsBad(value.StatusCode)
                ? CommResult.Fail(CommErrorKind.DeviceRejected, value.StatusCode.Code.ToString(CultureInfo.InvariantCulture), "The server reported a bad status code.")
                : CommResult.Ok();
        }
        catch (ServiceResultException ex)
        {
            return CommResult.Fail(MapServiceResult(ex), ex.StatusCode.ToString(CultureInfo.InvariantCulture), ex.Message);
        }
    }

    private static NodeId GetNodeId(DeviceAddress address) =>
        NodeId.Parse(address.Raw ?? throw new FormatException($"Address '{address}' lost its raw NodeId text."));

    private async Task<ApplicationConfiguration> BuildConfigurationAsync(CancellationToken ct)
    {
        var pkiRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IndustrialCommunication", "opcua-pki", Device.Name);

        var application = new ApplicationInstance(_telemetry)
        {
            ApplicationName = $"IndustrialCommunication.{Device.Name}",
            ApplicationType = ApplicationType.Client,
        };

        return await application
            .Build(
                $"urn:localhost:IndustrialCommunication:{Device.Name}",
                "urn:IndustrialCommunication:OpcUaDriver")
            .AsClient()
            .AddSecurityConfiguration(
                new CertificateIdentifierCollection
                {
                    new CertificateIdentifier
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = pkiRoot + "/own",
                        SubjectName = $"CN=IndustrialCommunication.{Device.Name}, DC=localhost",
                    },
                },
                pkiRoot)
            .SetAutoAcceptUntrustedCertificates(true)
            .CreateAsync(ct)
            .ConfigureAwait(false);
    }

    private async Task<ConfiguredEndpoint> SelectEndpointAsync(ApplicationConfiguration configuration, CancellationToken ct)
    {
        var description = await CoreClientUtils.SelectEndpointAsync(
            configuration, _options.EndpointUrl, _options.UseSecurity, _telemetry, ct).ConfigureAwait(false)
            ?? throw new CommunicationException(CommErrorKind.ConnectionLost, null,
                $"No OPC UA endpoint found at '{_options.EndpointUrl}'.");

        return new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(configuration));
    }

    private void OnKeepAlive(ISession session, KeepAliveEventArgs e)
    {
        if (ServiceResult.IsBad(e.Status))
            e.CancelKeepAlive = true; // stop UA keepalive; the unified reconnect path takes over
    }

    private static CommErrorKind MapServiceResult(ServiceResultException ex) => ex.StatusCode switch
    {
        StatusCodes.BadTimeout or StatusCodes.BadNotConnected
            => CommErrorKind.ConnectionLost,
        StatusCodes.BadSessionIdInvalid or StatusCodes.BadSecureChannelIdInvalid or StatusCodes.BadSecureChannelClosed
            => CommErrorKind.ConnectionLost,
        StatusCodes.BadNodeIdUnknown or StatusCodes.BadNodeIdInvalid
            => CommErrorKind.InvalidAddress,
        StatusCodes.BadTypeMismatch or StatusCodes.BadNotWritable or StatusCodes.BadUserAccessDenied
            => CommErrorKind.DeviceRejected,
        StatusCodes.BadCommunicationError or StatusCodes.BadConnectionClosed or StatusCodes.BadConnectionRejected
            => CommErrorKind.ConnectionLost,
        _ => CommErrorKind.ProtocolError,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "[{Device}] error while closing the OPC UA session")]
    private partial void LogCloseError(string device, Exception ex);

    /// <summary>
    /// Creates a native server-push subscription. Usage: create → Subscribe("ns=2;s=Tag") per node →
    /// ApplyChangesAsync → values arrive through the callback without polling.
    /// </summary>
    public async Task<OpcUaSubscription> CreateSubscriptionAsync(
        Action<OpcUaSubscription, OpcUaValueChangedEventArgs> onChanged,
        int publishingIntervalMs = 1000,
        CancellationToken ct = default)
    {
        var session = RequireSession();
        var subscription = new OpcUaSubscription(session, _telemetry, publishingIntervalMs, onChanged);
        _ = session.AddSubscription(subscription.UnderlyingSubscription);
        await subscription.CreateAsync(ct).ConfigureAwait(false);
        return subscription;
    }

    private ISession RequireSession() =>
        _session ?? throw new InvalidOperationException($"[{Device.Name}] OPC UA session is not open.");
}
