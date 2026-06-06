using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using KobraCache.Core.Models;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace KobraCache.Core.Cloud;

public sealed record AnycubicCloudMqttSession(string SessionToken, string UserId, string UserEmail);

public interface IAnycubicCloudMqttGateway
{
    Task<string> SendOrderAndWaitForResponseAsync(
        PrinterIdentity printer,
        AnycubicCloudMqttSession session,
        string action,
        Func<CancellationToken, Task<string?>> sendOrderAsync,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class MqttNetAnycubicCloudGateway : IAnycubicCloudMqttGateway
{
    private const string MqttHost = "mqtt-universe.anycubic.com";
    private const int MqttPort = 8883;
    private const string DeviceType = "pcf";

    public async Task<string> SendOrderAndWaitForResponseAsync(
        PrinterIdentity printer,
        AnycubicCloudMqttSession session,
        string action,
        Func<CancellationToken, Task<string?>> sendOrderAsync,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(printer.ModeId) || string.IsNullOrWhiteSpace(printer.CloudKey))
        {
            throw new InvalidOperationException("Cloud printer file commands require machine type and cloud key from Slicer cloud import.");
        }

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new MqttFactory();
        using var client = factory.CreateMqttClient();
        var orderMsgId = string.Empty;
        await using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        client.ApplicationMessageReceivedAsync += args =>
        {
            var text = Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment);
            if (IsExpectedResponse(text, action, orderMsgId))
            {
                completion.TrySetResult(text);
            }

            return Task.CompletedTask;
        };

        var options = BuildOptions(session);
        var printerSubscriptions = new[]
        {
            $"anycubic/anycubicCloud/v1/printer/app/{printer.ModeId}/{printer.CloudKey}/#",
            $"anycubic/anycubicCloud/v1/+/public/{printer.ModeId}/{printer.CloudKey}/#"
        };

        try
        {
            await client.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
            foreach (var topic in printerSubscriptions)
            {
                await client.SubscribeAsync(topic, MqttQualityOfServiceLevel.AtLeastOnce, cancellationToken).ConfigureAwait(false);
            }

            orderMsgId = await sendOrderAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static MqttClientOptions BuildOptions(AnycubicCloudMqttSession session)
    {
        var clientId = Md5Hex(session.UserEmail + DeviceType);
        var mqttToken = CreateMqttToken(session.SessionToken);
        var signature = Md5Hex(clientId + mqttToken + clientId);
        var username = $"user|{DeviceType}|{session.UserEmail}|{signature}";
        var certificate = LoadClientCertificate();

        return new MqttClientOptionsBuilder()
            .WithClientId(clientId)
            .WithTcpServer(MqttHost, MqttPort)
            .WithCredentials(username, mqttToken)
            .WithCleanSession()
            .WithTlsOptions(options =>
            {
                options.UseTls();
                options.WithSslProtocols(SslProtocols.Tls12);
                options.WithClientCertificates(new[] { certificate });
                options.WithAllowUntrustedCertificates();
                options.WithIgnoreCertificateChainErrors();
                options.WithIgnoreCertificateRevocationErrors();
            })
            .Build();
    }

    private static bool IsExpectedResponse(string text, string action, string orderMsgId)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var responseAction = GetString(root, "action");
            if (!string.Equals(responseAction, action, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var responseMsgId = GetString(root, "msgid") ?? GetString(root, "data", "msgid");
            if (!string.IsNullOrWhiteSpace(orderMsgId) &&
                !string.IsNullOrWhiteSpace(responseMsgId) &&
                !string.Equals(responseMsgId, orderMsgId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var state = GetString(root, "state");
            return string.IsNullOrWhiteSpace(state) ||
                   state.Equals("done", StringComparison.OrdinalIgnoreCase) ||
                   state.Equals("success", StringComparison.OrdinalIgnoreCase) ||
                   state.Equals("failed", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string CreateMqttToken(string sessionToken)
    {
        var caPem = ReadEmbeddedText("anycubic_mqtt_tls_ca.crt");
        using var caCertificate = X509Certificate2.CreateFromPem(caPem);
        using var rsa = caCertificate.GetRSAPublicKey()
            ?? throw new InvalidOperationException("Anycubic MQTT CA certificate does not include an RSA public key.");
        var encrypted = rsa.Encrypt(Encoding.UTF8.GetBytes(sessionToken), RSAEncryptionPadding.Pkcs1);
        return Convert.ToBase64String(encrypted);
    }

    private static X509Certificate2 LoadClientCertificate()
    {
        var certPem = ReadEmbeddedText("anycubic_mqtt_tls_client.crt");
        var keyPem = ReadEmbeddedText("anycubic_mqtt_tls_client.key");
        using var certificate = X509Certificate2.CreateFromPem(certPem, keyPem);
        return new X509Certificate2(certificate.Export(X509ContentType.Pfx));
    }

    private static string ReadEmbeddedText(string fileName)
    {
        var assembly = typeof(MqttNetAnycubicCloudGateway).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            throw new InvalidOperationException($"Missing embedded Anycubic MQTT resource: {fileName}.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Unable to read embedded Anycubic MQTT resource: {fileName}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Md5Hex(string value)
    {
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string? GetString(JsonElement item, params string[] path)
    {
        var current = item;
        foreach (var propertyName in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(propertyName, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }
}
