// MultiNodeTestRunner.exe command moved to the Akka.MultiNodeTestRunner project post-build event that is triggered by RUNMNTR constant.

using System;
using Akka.Actor;
using Akka.Cluster;
using Akka.Cluster.TestKit;
using Akka.Configuration;
using Akka.Event;
using Akka.Remote.TestKit;
using Xunit;
using Lighthouse;
using System.Linq;
using FluentAssertions;

namespace Akka.NetTests
{
    public class JoiningLighthouseTest : MultiNodeClusterSpec
    {
        public class JoiningLighthouseSpecConfig : MultiNodeConfig
        {
            public RoleName Node1 { get; }

            public RoleName Node2 { get; }

            public JoiningLighthouseSpecConfig()
            {
                Node1 = Role("node1");
                Node2 = Role("node2");

                CommonConfig = DebugConfig(true)
                .WithFallback(ConfigurationFactory.ParseString(@"
    akka.remote.retry-gate-closed-for = 5s
    akka.remote.log-remote-lifecycle-events = INFO
    akka.remote.dot-netty.connection-timeout = 25s
    akka.cluster.auto-down-unreachable-after = 10s
    akka.cluster.retry-unsuccessful-join-after = 3s
    akka.cluster.seed-node-timeout = 2s
    akka.cluster.shutdown-after-unsuccessful-join-seed-nodes = off
    akka.coordinated-shutdown.terminate-actor-system = on
    "))
                .WithFallback(MultiNodeClusterSpec.ClusterConfig());
            }
        }

        private const string _systemName = "SysWithLighthouse";
        private const int _actorSystem0Port = 666;
        private const int _actorSystem1Port = 667;
        private readonly TimeSpan _epsilonValueForWithins = TimeSpan.FromSeconds(1);
        private readonly JoiningLighthouseSpecConfig _config;
        private ActorSystem? _actorSystem;
        private ILoggingAdapter? _logger;
        private LighthouseService? _lighthouseService;
        private readonly Address _lighthouseAddress = new Address("akka.tcp", _systemName, "localhost", 9110);

        protected JoiningLighthouseTest(JoiningLighthouseSpecConfig config) : base(config, typeof(JoiningLighthouseTest))
        {
            _config = config;
        }

        public JoiningLighthouseTest() : this(new JoiningLighthouseSpecConfig()) { }

        protected override void AfterTermination()
        {
            if (_lighthouseService != null)
                _lighthouseService.StopAsync().Wait();
        }

        [MultiNodeFact]
        public void NodesShouldFormThreeMembersClusterByJoiningLighthouse()
        {
            Within(TimeSpan.FromSeconds(60), () =>
            {
                // Spawn an actor system on node 1.
                RunOn(() =>
                {
                    // I decided to use my own spawned actors. Not the test ones.
                    _actorSystem = ActorSystem.Create(_systemName,
                        ConfigurationFactory.ParseString("akka.remote.dot-netty.tcp.port = " + _actorSystem0Port)
                                            .WithFallback(Sys.Settings.Config));

                    _logger = Logging.GetLogger(_actorSystem, _actorSystem);

                    var actorSys0Addr = Akka.Cluster.Cluster.Get(_actorSystem).SelfAddress;
                    _logger.Debug($"#DEBUG# Actor System 0 address: {actorSys0Addr}");

                    Assert.Equal($"akka.tcp://{_systemName}@localhost:{_actorSystem0Port}", actorSys0Addr.ToString());
                }, _config.Node1);

                // Spawn an actor system on node 2.
                RunOn(() =>
                {
                    _actorSystem = ActorSystem.Create(_systemName,
                        ConfigurationFactory.ParseString("akka.remote.dot-netty.tcp.port = " + _actorSystem1Port)
                                            .WithFallback(Sys.Settings.Config));

                    _logger = Logging.GetLogger(_actorSystem, _actorSystem);

                    var actorSys1Addr = Akka.Cluster.Cluster.Get(_actorSystem).SelfAddress;
                    _logger.Debug($"#DEBUG# Actor System 1 address: {actorSys1Addr}");

                    Assert.Equal($"akka.tcp://{_systemName}@localhost:{_actorSystem1Port}", actorSys1Addr.ToString());
                }, _config.Node2);

                // Start Lighthouse on node 1.
                RunOn(() =>
                {
                    _lighthouseService = new LighthouseService("0.0.0.0", _lighthouseAddress.Port, _systemName);
                    _lighthouseService.Start();

                    EnterBarrier("lighthouse-started");

                    Akka.Cluster.Cluster.Get(_actorSystem).Join(_lighthouseAddress);
                    AwaitAssert(() => Akka.Cluster.Cluster.Get(_actorSystem).State.Members.Count().Should().Be(3));
                    AwaitAssert(() => Akka.Cluster.Cluster.Get(_actorSystem).State.Members.All(m => m.Status == MemberStatus.Up).Should().BeTrue());
                }, _config.Node1);

                RunOn(() =>
                {
                    EnterBarrier("lighthouse-started");

                    Akka.Cluster.Cluster.Get(_actorSystem).Join(_lighthouseAddress);
                }, _config.Node2);
            }, _epsilonValueForWithins);
        }
    }
}
