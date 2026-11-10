using System.Text.Json.Nodes;

namespace Shrink.Desktop;

public static class SelfTest
{
    public static async Task<int> Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "shrink-desktop-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var journal = new Journal(Path.Combine(root, "state.db"));
            var protectedConfig = new Configuration();
            protectedConfig.SetToken("test-device-secret");
            var serializedConfig = System.Text.Json.JsonSerializer.Serialize(protectedConfig);
            if (serializedConfig.Contains("test-device-secret") || JsonNode.Parse(serializedConfig)!["Token"] != null)
                throw new Exception("Decrypted device credential serialized");
            var request = new JsonObject { ["id"] = "one", ["action"] = "sessions" };
            if (!journal.Begin("one", request).Fresh) throw new Exception("Fresh command rejected");
            if (journal.Begin("one", request).Fresh) throw new Exception("Duplicate command executed");
            journal.Complete("one", new JsonObject { ["ok"] = true });
            if (journal.Begin("one", request).Result?["ok"]?.GetValue<bool>() != true) throw new Exception("Result lost");
            try { journal.Begin("one", new JsonObject { ["id"] = "one", ["action"] = "start" }); throw new Exception("Conflict accepted"); }
            catch (InvalidOperationException) { }
            journal.Begin("crash", new JsonObject { ["action"] = "send" });
            journal = new Journal(Path.Combine(root, "state.db"));
            if (journal.Begin("crash", new JsonObject { ["action"] = "send" }).Result?["ok"]?.GetValue<bool>() != false) throw new Exception("Uncertain write retried");
            var seq = journal.Emit(new JsonObject { ["kind"] = "test" });
            if (journal.Events(0).Count != 1 || journal.Events(seq).Count != 0) throw new Exception("Event replay invalid");
            var controller = new Controller(new Configuration(), journal) { Paused = true };
            var denied = await controller.Execute(new JsonObject { ["id"] = "pause", ["action"] = "start" });
            if (denied["ok"]?.GetValue<bool>() != false) throw new Exception("Pause bypass");
            Console.WriteLine("PASS: durability, deduplication, conflicts, crash recovery, event replay, pause");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    public static async Task<int> Agent(string kind)
    {
        var config = Configuration.Load();
        var root = Path.Combine(Path.GetTempPath(), "shrink-agent-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = new Journal(Path.Combine(root, "state.db"));
        await using var session = new AgentSession(kind, root, config.Agents[kind], journal);
        try
        {
            await session.Start();
            Console.WriteLine("START " + session.Snapshot().ToJsonString());
            foreach (var word in new[] { "SHRINK_CONNECTED", "SHRINK_CONTINUED" })
            {
                await session.Send($"Connection test only. Do not call tools or modify files. Reply exactly {word}.");
                var until = DateTime.UtcNow.AddSeconds(120);
                while (session.State is "running" && DateTime.UtcNow < until) await Task.Delay(500);
                Console.WriteLine("RESULT " + session.Snapshot().ToJsonString());
                if (session.State != "completed" || !session.Snapshot()["output"]!.GetValue<string>().Contains(word)) return 1;
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
