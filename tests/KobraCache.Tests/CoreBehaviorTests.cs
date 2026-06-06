using System.Net;
using System.Text;
using System.Text.Json;
using KobraCache.Core.Cloud;
using KobraCache.Core.Models;
using KobraCache.Core.Services;
using KobraCache.Core.Slicer;
using KobraCache.Core.Transports;

namespace KobraCache.Tests;

public sealed class CoreBehaviorTests
{
    [Fact]
    public async Task SlicerImporter_reads_token_and_decodes_lan_machine_list()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"kobracache-{Guid.NewGuid():N}.conf");
        var machineListJson = """
        [
          {
            "ip": "192.168.9.183",
            "deviceId": "device-1",
            "modeId": "kobra-s1",
            "name": "Shop S1",
            "modelName": "Kobra S1",
            "username": "mqtt-user",
            "password": "mqtt-pass"
          }
        ]
        """;
        var encodedMachineList = EncodeAnycubicValue(machineListJson);
        var config = $$"""
        {
          "anycubic_cloud": {
            "access_token": "cloud-token"
          },
          "anycubic_remote_printing": {
            "machine_list_of_LAN": {{JsonSerializer.Serialize(encodedMachineList)}}
          }
        }
        trailing checksum
        """;

        try
        {
            await File.WriteAllTextAsync(tempFile, config);

            var result = await new SlicerConfigImporter().ImportAsync(tempFile);

            Assert.Equal("cloud-token", result.CloudAccessToken);
            var printer = Assert.Single(result.LanPrinters);
            Assert.Equal("192.168.9.183", printer.IpAddress);
            Assert.Equal("device-1", printer.DeviceId);
            Assert.Equal("kobra-s1", printer.ModeId);
            Assert.Equal("mqtt-user", printer.MqttUsername);
            Assert.Equal("mqtt-pass", printer.MqttPassword);
            Assert.True(printer.HasLanCredentials);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SlicerImporter_extracts_first_json_object_with_string_braces()
    {
        var content = """
        prefix
        {"value":"text with } brace","nested":{"ok":true}}
        checksum
        """;

        var extracted = SlicerConfigImporter.ExtractFirstJsonObject(content);

        Assert.Equal("""{"value":"text with } brace","nested":{"ok":true}}""", extracted);
    }

    [Fact]
    public void ManualPrinterService_accepts_valid_ip_and_rejects_invalid_ip()
    {
        var service = new ManualPrinterService();

        var printer = service.AddManualIp(" 192.168.9.170 ");

        Assert.Equal("192.168.9.170", printer.IpAddress);
        Assert.Equal(PrinterConnectionMode.ProbeOnly, printer.ConnectionMode);
        Assert.Throws<ArgumentException>(() => service.AddManualIp("not-an-ip"));
    }

    [Fact]
    public void RetentionFilter_requires_idle_status_and_excludes_current_new_and_undated_files()
    {
        var printer = TestPrinter();
        var now = new DateTimeOffset(2026, 6, 6, 12, 0, 0, TimeSpan.Zero);
        var files = new[]
        {
            TestFile("old.gcode", now.AddDays(-31)),
            TestFile("new.gcode", now.AddDays(-2)),
            TestFile("current.gcode", now.AddDays(-90), isCurrent: true),
            TestFile("undated.gcode", null)
        };
        var policy = new RetentionPolicy { Preset = RetentionPreset.Days30 };

        var idlePreview = new RetentionFilter().CreatePreview(printer, PrinterRuntimeStatus.Idle, files, policy, now);
        var busyPreview = new RetentionFilter().CreatePreview(printer, PrinterRuntimeStatus.Busy, files, policy, now);

        Assert.Equal(["old.gcode"], idlePreview.Items.Where(item => item.IsEligible).Select(item => item.File.FileName).ToArray());
        Assert.Contains(idlePreview.Items, item => item.File.FileName == "undated.gcode" && item.Reason == "No reliable file date.");
        Assert.Contains(idlePreview.Items, item => item.File.FileName == "current.gcode" && item.Reason == "Current or active print file.");
        Assert.Contains(idlePreview.Items, item => item.File.FileName == "new.gcode" && item.Reason == "Newer than selected cutoff.");
        Assert.All(busyPreview.Items, item => Assert.Equal("Printer is not confirmed idle.", item.Reason));
    }

    [Fact]
    public async Task LanMqttPrinterClient_sends_expected_file_actions_with_fake_gateway()
    {
        var gateway = new FakeLanGateway();
        var client = new LanMqttPrinterClient(gateway);
        var printer = TestLanPrinter();
        gateway.Responses.Enqueue("""{"action":"listLocal","data":{"files":[{"filename":"local-old.gcode","path":"/local","size":2048,"update_time":1700000000}]}}""");
        gateway.Responses.Enqueue("""{"action":"listUdisk","data":{"files":[{"filename":"usb-old.gcode","path":"/usb","size":4096,"update_time":1700000001}]}}""");
        gateway.Responses.Enqueue("""{"action":"deleteLocal","data":{"result":0}}""");
        gateway.Responses.Enqueue("""{"action":"deleteUdisk","data":{"result":0}}""");

        var localFiles = await client.ListFilesAsync(printer, StorageTarget.LocalCache);
        var usbFiles = await client.ListFilesAsync(printer, StorageTarget.Usb);
        await client.DeleteFileAsync(printer, localFiles.Single());
        await client.DeleteFileAsync(printer, usbFiles.Single());

        Assert.Equal(["listLocal", "listUdisk", "deleteLocal", "deleteUdisk"], gateway.Actions);
        Assert.Equal("local-old.gcode", localFiles.Single().FileName);
        Assert.Equal(StorageTarget.LocalCache, localFiles.Single().StorageTarget);
        Assert.Equal("usb-old.gcode", usbFiles.Single().FileName);
        Assert.Equal(StorageTarget.Usb, usbFiles.Single().StorageTarget);
        Assert.Contains(gateway.Payloads, payload => payload.Contains("\"filename\":\"local-old.gcode\"", StringComparison.Ordinal));
        Assert.Contains(gateway.Payloads, payload => payload.Contains("\"filename\":\"usb-old.gcode\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnycubicCloudClient_imports_status_lists_and_deletes_with_token_header()
    {
        var handler = new StubHttpHandler(request =>
        {
            Assert.Equal("token-123", request.Headers.Authorization?.Parameter);
            Assert.True(request.Headers.Contains("XX-Token"));

            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/work/printer/getPrinters", StringComparison.Ordinal))
            {
                return JsonResponse("""{"data":[{"id":"printer-1","key":"cloud-key","name":"Shop S1","model":"Kobra S1","machine_data":{"ip":"192.168.9.213"}}]}""");
            }

            if (path.EndsWith("/work/printer/printersStatus", StringComparison.Ordinal))
            {
                return JsonResponse("""{"data":[{"id":"printer-1","status":"idle"}]}""");
            }

            if (path.EndsWith("/work/index/files", StringComparison.Ordinal))
            {
                return JsonResponse("""{"data":{"list":[{"id":"file-1","filename":"cloud-old.gcode","size":1000,"update_time":1700000000}]}}""");
            }

            if (path.EndsWith("/work/index/delFiles", StringComparison.Ordinal))
            {
                return JsonResponse("""{"code":0}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/p/p/workbench/api/") };
        var cloudClient = new AnycubicCloudClient(httpClient);

        var printers = await cloudClient.ListPrintersAsync("token-123");
        var printer = Assert.Single(printers);
        var status = await cloudClient.GetStatusAsync(printer);
        var files = await cloudClient.ListFilesAsync(printer, StorageTarget.Cloud);
        await cloudClient.DeleteFileAsync(printer, files.Single());

        Assert.Equal("Shop S1", printer.DisplayName);
        Assert.Equal("192.168.9.213", printer.IpAddress);
        Assert.Equal("token-123", printer.CloudAccessToken);
        Assert.Equal(PrinterRuntimeStatus.Idle, status);
        Assert.Equal("cloud-old.gcode", files.Single().FileName);
        Assert.Equal(StorageTarget.Cloud, files.Single().StorageTarget);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/work/index/delFiles", StringComparison.Ordinal));
    }

    private static PrinterIdentity TestPrinter()
    {
        return new PrinterIdentity
        {
            Key = "test-printer",
            DisplayName = "Test Printer",
            Source = PrinterSource.ManualIp,
            ConnectionMode = PrinterConnectionMode.ProbeOnly
        };
    }

    private static PrinterIdentity TestLanPrinter()
    {
        return new PrinterIdentity
        {
            Key = "lan-printer",
            DisplayName = "LAN Printer",
            IpAddress = "192.168.9.183",
            Source = PrinterSource.SlicerLan,
            ConnectionMode = PrinterConnectionMode.LanMqtt,
            ModeId = "mode-1",
            DeviceId = "device-1",
            MqttUsername = "user",
            MqttPassword = "password"
        };
    }

    private static PrinterCacheFile TestFile(string fileName, DateTimeOffset? modifiedAt, bool isCurrent = false)
    {
        return new PrinterCacheFile
        {
            PrinterKey = "test-printer",
            StorageTarget = StorageTarget.LocalCache,
            FileName = fileName,
            ModifiedAt = modifiedAt,
            IsCurrentJob = isCurrent
        };
    }

    private static string EncodeAnycubicValue(string json)
    {
        var secondPayload = Encoding.UTF8.GetBytes(json).Select(value => unchecked((byte)(value + 5))).ToArray();
        var secondText = Convert.ToBase64String(secondPayload).TrimEnd('=');
        var firstPayload = Encoding.ASCII.GetBytes(secondText).Select(value => unchecked((byte)(value + 5))).ToArray();
        return Convert.ToBase64String(firstPayload).TrimEnd('=');
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class FakeLanGateway : ILanMqttGateway
    {
        public Queue<string> Responses { get; } = new();

        public List<string> Actions { get; } = [];

        public List<string> Payloads { get; } = [];

        public Task<string> SendCommandAsync(
            PrinterIdentity printer,
            string action,
            object payload,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Actions.Add(action);
            Payloads.Add(JsonSerializer.Serialize(payload));
            return Task.FromResult(Responses.Dequeue());
        }
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_handler(request));
        }
    }
}
