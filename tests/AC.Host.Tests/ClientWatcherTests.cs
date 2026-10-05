using System;
using System.Collections.Generic;
using System.Linq;
using AC.Host.Runtime;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// When the Decal Agent puts the overlay into the client by itself: once per client, after
    /// its window has settled, and never again unasked. No real process is looked at or touched.
    /// </summary>
    public class ClientWatcherTests
    {
        private static readonly DateTime Start = new DateTime(2026, 9, 29, 20, 0, 0, DateTimeKind.Utc);

        private sealed class FakeClients
        {
            public List<ClientWindow> Running { get; } = new List<ClientWindow>();

            public List<int> Injected { get; } = new List<int>();

            public bool Succeed { get; set; } = true;

            public IReadOnlyList<ClientWindow> Find() => Running.ToList();

            public ClientInjection Inject(int pid)
            {
                Injected.Add(pid);
                return new ClientInjection(pid, Succeed, Succeed ? "loaded" : "refused");
            }
        }

        private static (FakeClients Clients, ClientWatcher Watcher) Watch()
        {
            FakeClients clients = new FakeClients();
            return (clients, new ClientWatcher(clients.Inject, clients.Find) { Settle = TimeSpan.FromSeconds(5) });
        }

        [Fact]
        public void TheClientIsInjectedOnceItsWindowHasSettledAndOnlyOnce()
        {
            (FakeClients clients, ClientWatcher watcher) = Watch();
            Assert.Null(watcher.Poll(Start));
            Assert.Null(watcher.Client);

            clients.Running.Add(new ClientWindow(100, "AC:Unreal (64-bit)"));
            Assert.Null(watcher.Poll(Start.AddSeconds(1)));
            Assert.Null(watcher.Poll(Start.AddSeconds(4)));
            Assert.Equal(100, watcher.Client?.ProcessId);
            Assert.Empty(clients.Injected);

            ClientInjection outcome = watcher.Poll(Start.AddSeconds(6));
            Assert.True(outcome.Success);
            Assert.Equal(new[] { 100 }, clients.Injected);
            Assert.Same(outcome, watcher.Outcome);

            for (int s = 7; s < 60; s++)
                Assert.Null(watcher.Poll(Start.AddSeconds(s)));
            Assert.Single(clients.Injected);
        }

        [Fact]
        public void AFailureIsReportedNotRetriedUntilThePlayerAsks()
        {
            (FakeClients clients, ClientWatcher watcher) = Watch();
            clients.Succeed = false;
            clients.Running.Add(new ClientWindow(100, "AC:Unreal"));
            watcher.Poll(Start);

            ClientInjection failed = watcher.Poll(Start.AddSeconds(10));
            Assert.False(failed.Success);
            Assert.Null(watcher.Poll(Start.AddSeconds(30)));
            Assert.Single(clients.Injected);

            List<ClientInjection> heard = new List<ClientInjection>();
            watcher.Attempted += (_, e) => heard.Add(e);
            clients.Succeed = true;
            ClientInjection asked = watcher.InjectNow();

            Assert.True(asked.Success);
            Assert.Equal(new[] { 100, 100 }, clients.Injected);
            Assert.Same(asked, Assert.Single(heard));
        }

        [Fact]
        public void ARestartedClientIsANewClient()
        {
            (FakeClients clients, ClientWatcher watcher) = Watch();
            clients.Running.Add(new ClientWindow(100, "AC:Unreal"));
            watcher.Poll(Start);
            watcher.Poll(Start.AddSeconds(5));

            clients.Running.Clear();
            watcher.Poll(Start.AddSeconds(20));
            Assert.Null(watcher.Client);
            Assert.Null(watcher.Outcome);

            // The same id again, as the system may hand out, and a different one.
            clients.Running.Add(new ClientWindow(100, "AC:Unreal"));
            watcher.Poll(Start.AddSeconds(30));
            watcher.Poll(Start.AddSeconds(36));

            Assert.Equal(new[] { 100, 100 }, clients.Injected);
        }

        [Fact]
        public void WithAutomaticInjectionOffTheClientIsWatchedButLeftAlone()
        {
            (FakeClients clients, ClientWatcher watcher) = Watch();
            watcher.AutoInject = false;
            clients.Running.Add(new ClientWindow(100, "AC:Unreal"));

            watcher.Poll(Start);
            watcher.Poll(Start.AddMinutes(5));

            Assert.Equal(100, watcher.Client?.ProcessId);
            Assert.Empty(clients.Injected);
            Assert.True(watcher.InjectNow().Success);
        }

        [Fact]
        public void OnlyTheFirstOfTwoClientsIsInjectedInto()
        {
            (FakeClients clients, ClientWatcher watcher) = Watch();
            clients.Running.Add(new ClientWindow(300, "AC:Unreal"));
            watcher.Poll(Start);
            clients.Running.Add(new ClientWindow(200, "AC:Unreal"));
            watcher.Poll(Start.AddSeconds(2));

            watcher.Poll(Start.AddSeconds(20));

            Assert.Equal(2, watcher.ClientCount);
            Assert.Equal(300, watcher.Client?.ProcessId);
            Assert.Equal(new[] { 300 }, clients.Injected);
        }

        [Fact]
        public void AskingWithNoClientRunningSaysSoAndTouchesNothing()
        {
            (FakeClients clients, ClientWatcher watcher) = Watch();

            ClientInjection outcome = watcher.InjectNow();

            Assert.False(outcome.Success);
            Assert.Contains("not running", outcome.Message);
            Assert.Empty(clients.Injected);
        }

        [Fact]
        public void AskingBeforeTheFirstLookFindsTheClientAndInjectsOnce()
        {
            (FakeClients clients, ClientWatcher watcher) = Watch();
            clients.Running.Add(new ClientWindow(42, "AC:Unreal"));

            Assert.True(watcher.InjectNow().Success);
            Assert.Null(watcher.Poll(DateTime.UtcNow.AddMinutes(1)));

            Assert.Equal(new[] { 42 }, clients.Injected);
        }

        [Fact]
        public void AnInjectorThatThrowsIsAFailureWithItsReason()
        {
            ClientWatcher watcher = new ClientWatcher(_ => throw new UnauthorizedAccessException("access denied"), () => new[] { new ClientWindow(7, "AC:Unreal") })
            {
                Settle = TimeSpan.Zero,
            };

            ClientInjection outcome = watcher.Poll(Start);

            Assert.False(outcome.Success);
            Assert.Contains("access denied", outcome.Message);
            Assert.Null(watcher.Poll(Start.AddSeconds(1)));
        }
    }
}
