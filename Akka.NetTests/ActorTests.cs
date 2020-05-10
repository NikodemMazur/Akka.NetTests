using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Akka.Actor;
using Akka.Event;
using Akka.TestKit;
using Xunit;

namespace Akka.NetTests
{
    public class ActorTests : TestKit.Xunit2.TestKit
    {
        public sealed class VariableBehaviorActor : ReceiveActor
        {
            public VariableBehaviorActor()
            {
                Receive<string>(_ =>
                {
                    Sender.Tell("Ctor behavior.", Self);
                    Become(SecondBehavior); // Never call Become from ctor. It has no effects then.
                });
            }

            void SecondBehavior()
            {
                Receive<string>(_ =>
                {
                    Sender.Tell("Other behavior.", Self);
                });
            }
        }

        public sealed class IntenseComputationActor : ReceiveActor
        {
            public IntenseComputationActor()
            {
                Receive<string>(_ =>
                {
                    Thread.Sleep(1000);
                    Sender.Tell("Intense data.", Self);
                });
            }
        }

        public sealed class EchoActor : UntypedActor
        {
            protected override void OnReceive(object message) =>
                Sender.Tell(message, Self);
        }

        public sealed class SelfStoppingEchoActor : UntypedActor
        {
            protected override void OnReceive(object message)
            {
                Sender.Tell(message, Self);
                Become(Failing);
            }

            void Failing(object message)
            {
                try
                {
                    throw new InvalidOperationException();
                }
                catch
                {
                    Context.Stop(Self); // Finishes processing of current msg, then terminates.
                }
            }
        }

        public sealed class FailingActor : UntypedActor
        {
            protected override void OnReceive(object message) =>
                throw new NotImplementedException();
        }

        public sealed class FrozenActor : UntypedActor
        {
            protected override void OnReceive(object message)
            {
                if (message as string != "Freeze!")
                    Sender.Tell(message, Self);
                else
                {
                    var cts = new CancellationTokenSource();
                    Sender.Tell(cts, Self);
                    try
                    {
                        Task.Delay((int) TimeSpan.FromMinutes(7).TotalMilliseconds, cts.Token)
                            .Wait();
                    }
                    catch
                    {
                        Context.Stop(Self);
                    }
                }
            }
        }

        public sealed class StashingActor : UntypedActor, IWithUnboundedStash
        {
            protected override void OnReceive(object message)
            {
                if ((message as string)?.Contains("stash", StringComparison.OrdinalIgnoreCase) ?? false)
                    Stash.Stash();
                else
                    Sender.Tell(message, Self);
            }

            public IStash Stash { get; set; }
        }

        public sealed class ForwardingActor : UntypedActor
        {
            private readonly IActorRef _recipient;

            public ForwardingActor(IActorRef recipient)
            {
                _recipient = recipient;
            }

            protected override void OnReceive(object message)
            {
                _recipient.Forward(message);
            }
        }

        [Fact]
        public async void ActorInitialBehaviorComesFromCtor()
        {
            Props autProps = Props.Create<VariableBehaviorActor>();
            IActorRef aut = Sys.ActorOf(autProps);

            string answer = await aut.Ask<string>(string.Empty, TimeSpan.FromMilliseconds(100));
            Assert.Contains("ctor", answer, StringComparison.OrdinalIgnoreCase);

            answer = await aut.Ask<string>(string.Empty, TimeSpan.FromMilliseconds(100));
            Assert.Contains("other", answer, StringComparison.OrdinalIgnoreCase);

            answer = await aut.Ask<string>(string.Empty, TimeSpan.FromMilliseconds(100));
            Assert.Contains("other", answer, StringComparison.OrdinalIgnoreCase);

            ExpectNoMsg(TimeSpan.FromMilliseconds(250));
        }

        [Fact]
        public void TestKitWatchesDeath()
        {
            var notSoTalkativeActor = Sys.ActorOf(Props.Empty);

            Watch(notSoTalkativeActor);

            ExpectNoMsg(TimeSpan.FromMilliseconds(150));

            notSoTalkativeActor.Tell(PoisonPill.Instance);

            ExpectMsg<Terminated>(msg => msg.ActorRef.Equals(notSoTalkativeActor));

            Unwatch(notSoTalkativeActor);
        }

        [Fact]
        public void DeathWatchGetsTerminatedMsgOnActorKill()
        {
            var aut = Sys.ActorOf(Props.Empty);

            Watch(aut);

            aut.Tell(Kill.Instance, ActorRefs.NoSender);

            ExpectTerminated(aut);

            Unwatch(aut);
        }

        [Fact]
        public void TestKitWaitsLongEnoughForMsg()
        {
            var longAnsweringActor = Sys.ActorOf<IntenseComputationActor>();

            longAnsweringActor.Tell(string.Empty);

            ExpectMsg<string>("Intense data.");
        }

        [Fact]
        public async void ActorStopsRespondingAfterBeingKilled()
        {
            var aut = Sys.ActorOf<EchoActor>();

            var result = await aut.Ask<string>("Echo.", TimeSpan.FromMilliseconds(50));

            Assert.Equal("Echo.", result);

            Watch(aut);

            aut.Tell(Kill.Instance, ActorRefs.NoSender);

            ExpectTerminated(aut); // Wait for kill.

            aut.Tell("Second echo.");

            ExpectNoMsg(TimeSpan.FromMilliseconds(1000));

            Unwatch(aut);
        }

        [Fact]
        public async void NonAkkaTestAwaitCollectsAggregateExOnCancellation()
        {
            var cts = new CancellationTokenSource();

            var task = LongRunningMethod(cts.Token);

            cts.CancelAfter(500);

            var ex = await Assert.ThrowsAsync<AggregateException>(() => task);

            Assert.Contains(ex.Flatten().InnerExceptions, ex_ => ex_ is TaskCanceledException);

            static Task<bool> LongRunningMethod(CancellationToken cancellationToken)
            {
                var tcs = new TaskCompletionSource<bool>();

                Task.Run(() =>
                {
                    try
                    {
                        Task.WaitAll(Task.Delay(1000, cancellationToken),
                            Task.Run(() => throw new OperationCanceledException()));
                    }
                    catch (Exception e)
                    {
                        tcs.SetException(e);
                    }
                    tcs.SetResult(true);
                });

                return tcs.Task;
            }
        }

        [Fact]
        public async void ActorStopsRespondingAfterBeingPoisoned()
        {
            var aut = Sys.ActorOf<EchoActor>();

            var result = await aut.Ask<string>("Echo.", TimeSpan.FromMilliseconds(50));

            Assert.Equal("Echo.", result);

            Watch(aut);

            aut.Tell(PoisonPill.Instance, ActorRefs.NoSender);

            ExpectMsg<Terminated>(); // Wait for kill.

            aut.Tell("Second echo.");

            ExpectNoMsg(TimeSpan.FromMilliseconds(1000));

            Unwatch(aut);
        }

        [Fact]
        public async void ActorStopsRespondingAfterThrowingEx()
        {
            var aut = Sys.ActorOf<SelfStoppingEchoActor>();

            var result = await aut.Ask<string>("Echo.", TimeSpan.FromMilliseconds(50));

            Assert.Equal("Echo.", result);

            await Assert.ThrowsAsync<AskTimeoutException>(() => 
                aut.Ask<string>("Echo.", TimeSpan.FromMilliseconds(100)));

            await Assert.ThrowsAsync<AskTimeoutException>(() =>
                aut.Ask<string>("Echo.", TimeSpan.FromMilliseconds(100)));
        }

        [Fact]
        public void UserChildRestartsByDefault()
        {
            var aut = Sys.ActorOf<FailingActor>();

            Watch(aut);

            aut.Tell(string.Empty);
            aut.Tell(string.Empty);
            aut.Tell(string.Empty);

            ExpectNoMsg(TimeSpan.FromMilliseconds(500)); // No Terminated message.

            Unwatch(aut);
        }

        [Fact]
        public void MessageBecomesDeadLetterAfterActorFail()
        {
            var failingActor = Sys.ActorOf<SelfStoppingEchoActor>();
            var probe = CreateTestProbe();

            Sys.EventStream.Subscribe(probe.Ref, typeof(DeadLetter));

            failingActor.Tell("Non-dead letter.");
            ExpectMsg("Non-dead letter.");

            failingActor.Tell("This msg will stop you!");

            failingActor.Tell("Dead letter.");

            probe.ExpectMsg<DeadLetter>(dl => 
                (string)dl.Message == "Dead letter.");

            Sys.EventStream.Unsubscribe(probe.Ref);
        }

        [Fact]
        public void StashBecomesDeadLettersWhenActorIsKilled()
        {
            var aut = Sys.ActorOf<StashingActor>();
            var probe = CreateTestProbe();

            Sys.EventStream.Subscribe(probe.Ref, typeof(DeadLetter));

            aut.Tell("Hi, stash this message.");
            aut.Tell("Stash this as well.");

            ExpectNoMsg(TimeSpan.FromMilliseconds(100));

            Watch(aut);

            aut.Tell(PoisonPill.Instance, ActorRefs.NoSender);

            ExpectTerminated(aut);

            Unwatch(aut);

            probe.ExpectMsg<DeadLetter>(dl => 
                (string)dl.Message == "Hi, stash this message.");
            probe.ExpectMsg<DeadLetter>(dl =>
                (string)dl.Message == "Stash this as well.");

            Sys.EventStream.Unsubscribe(probe.Ref);
        }

        [Fact]
        public void UnprocessedMailboxBecomesDeadLetters()
        {
            var probe = CreateTestProbe();
            Sys.EventStream.Subscribe(probe.Ref, typeof(DeadLetter));

            var frozenActor = Sys.ActorOf<FrozenActor>();

            Watch(frozenActor);

            frozenActor.Tell("Freeze!");
            frozenActor.Tell("First message that stays in the mailbox.");
            frozenActor.Tell("Second message that stays in the mailbox.");
            frozenActor.Tell("Third message that stays in the mailbox.");

            var cts = ExpectMsg<CancellationTokenSource>();
            cts.Cancel(); // Now, the frozen actor should stop itself.

            ExpectTerminated(frozenActor); // Assert termination.

            Unwatch(frozenActor);

            probe.ExpectMsg<DeadLetter>(dl =>
                (string)dl.Message == "First message that stays in the mailbox.");
            probe.ExpectMsg<DeadLetter>(dl =>
                (string)dl.Message == "Second message that stays in the mailbox.");
            probe.ExpectMsg<DeadLetter>(dl =>
                (string)dl.Message == "Third message that stays in the mailbox.");

            Sys.EventStream.Unsubscribe(probe.Ref);
        }

        [Fact]
        public void ForwardingActorForwardsScheduledMessagesToTestActor()
        {
            var forwardingActor = Sys.ActorOf(Props.Create<ForwardingActor>(TestActor));

            Sys.Scheduler.ScheduleTellRepeatedly(TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(500), forwardingActor, "Cyclic message.", ActorRefs.NoSender);

            ExpectNoMsg(TimeSpan.FromMilliseconds(90));

            ExpectMsg("Cyclic message.", TimeSpan.FromMilliseconds(520));

            ExpectMsg("Cyclic message.", TimeSpan.FromMilliseconds(520));

            ExpectNoMsg(TimeSpan.FromMilliseconds(490));
        }

        [Fact]
        public void FsmActorWorksAsExpected()
        {
            var system = ActorSystem.Create("mySystem");
            
        }

        [Fact]
        public void EscalatesOnlyNullToSupervisor()
        {

        }

        [Fact]
        public void DeathWatchHooksIntoActorsByActorSelection()
        {

        }

        [Fact]
        public void CoordinatedShutdownTerminatesActorSystemGracefully()
        {

        }
    }
}
