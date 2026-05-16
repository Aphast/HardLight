using Content.Server.GameTicking;
using Content.Server.GameTicking.Rules;
using Content.Server.GameTicking.Rules.Components;
using Content.Shared.GameTicking.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;
using System.IO;

namespace Content.IntegrationTests.Tests.GameRules
{
    [TestFixture]
    [TestOf(typeof(MaxTimeRestartRuleSystem))]
    public sealed class RuleMaxTimeRestartTest
    {
        private static void WriteProbe(string message)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "rulemaxrestart.probe");
            File.AppendAllText(path, message + "\n");
            TestContext.Progress.WriteLine(message);
        }

        [Test]
        public async Task RestartTest()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
            var server = pair.Server;

            Assert.That(server.EntMan.Count<GameRuleComponent>(), Is.Zero);
            Assert.That(server.EntMan.Count<ActiveGameRuleComponent>(), Is.Zero);

            var entityManager = server.ResolveDependency<IEntityManager>();
            var sGameTicker = server.ResolveDependency<IEntitySystemManager>().GetEntitySystem<GameTicker>();
            var sGameTiming = server.ResolveDependency<IGameTiming>();

            MaxTimeRestartRuleComponent maxTime = null;
            await server.WaitPost(() =>
            {
                sGameTicker.StartGameRule("NFMaxTimeRestart", out var ruleEntity); // Frontier: use NF variant (upstream abstracted)
                Assert.That(entityManager.TryGetComponent<MaxTimeRestartRuleComponent>(ruleEntity, out maxTime));
            });

            WriteProbe("RuleMaxTimeRestartTest: rule started");

            Assert.That(server.EntMan.Count<GameRuleComponent>(), Is.EqualTo(1));
            Assert.That(server.EntMan.Count<ActiveGameRuleComponent>(), Is.EqualTo(1));

            await server.WaitAssertion(() =>
            {
                Assert.That(sGameTicker.RunLevel, Is.EqualTo(GameRunLevel.PreRoundLobby));
                maxTime.RoundMaxTime = TimeSpan.FromSeconds(3);
                sGameTicker.StartRound();
            });

            WriteProbe("RuleMaxTimeRestartTest: start round requested");

            Assert.That(server.EntMan.Count<GameRuleComponent>(), Is.EqualTo(1));
            Assert.That(server.EntMan.Count<ActiveGameRuleComponent>(), Is.EqualTo(1));

            await server.WaitAssertion(() =>
            {
                Assert.That(sGameTicker.RunLevel, Is.EqualTo(GameRunLevel.InRound));
            });

            WriteProbe("RuleMaxTimeRestartTest: reached in-round");

            var ticks = sGameTiming.TickRate * (int) Math.Ceiling(maxTime.RoundMaxTime.TotalSeconds * 1.1f);
            await pair.RunTicksSync(ticks);

            WriteProbe("RuleMaxTimeRestartTest: completed max-time ticks");

            await server.WaitAssertion(() =>
            {
                Assert.That(sGameTicker.RunLevel, Is.EqualTo(GameRunLevel.PostRound));
            });

            WriteProbe("RuleMaxTimeRestartTest: reached post-round");

            ticks = sGameTiming.TickRate * (int) Math.Ceiling(maxTime.RoundEndDelay.TotalSeconds * 1.1f);
            await pair.RunTicksSync(ticks);

            WriteProbe("RuleMaxTimeRestartTest: completed restart-delay ticks");

            await server.WaitAssertion(() =>
            {
                Assert.That(sGameTicker.RunLevel, Is.EqualTo(GameRunLevel.PreRoundLobby));
            });

            WriteProbe("RuleMaxTimeRestartTest: returned to pre-round lobby");

            await pair.CleanReturnAsync();
        }
    }
}
