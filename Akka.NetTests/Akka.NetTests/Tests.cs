using Akka.TestKit;
using Xunit;
using System;
using Akka.Streams;
using Akka.Streams.Dsl;
using System.Threading.Tasks;
using Akka.Streams.Implementation.Fusing;
using System.Threading;
using System.Linq;
using System.Collections.Generic;
using Akka.Actor;

namespace Akka.NetTests
{
    public class Tests : TestKit.Xunit2.TestKit
    {
        TimeSpan _epsilonValueForWithins;
        readonly ActorMaterializer _materializer;

        public Tests()
        {
            _materializer = Sys.Materializer();
            _epsilonValueForWithins = TimeSpan.FromSeconds(1);
        }

        [Fact]
        public void MaybeViaFluentApiMaterializedToBothWaitsForTcs()
        {
            Source<string, TaskCompletionSource<string>> source = Source.Maybe<string>();
            Sink<string, Task<string>> sink = Sink.First<string>();
            Aggregate<string, string> flow = new Aggregate<string, string>("Start, ", (agg, curr) => agg + curr);

            IRunnableGraph<(TaskCompletionSource<string>, Task<string>)> rg = source.Via(flow).ToMaterialized(sink, Keep.Both);

            (TaskCompletionSource<string> tcs, Task<string> task) = rg.Run(_materializer);

            Thread.Sleep(TimeSpan.FromMilliseconds(500));
            Assert.False(task.IsCompleted);
            tcs.SetResult("End!");
            task.Wait();
            Assert.True(task.IsCompleted);
            Assert.Equal("Start, End!", task.Result);
        }

        [Fact]
        public void RunnableGraphByGraphApiDoesntThrowEx()
        {
            IGraph<ClosedShape, NotUsed> gdsl = GraphDsl.Create(builder =>
            {
                Source<int, NotUsed> source = Source.From(Enumerable.Range(1, 100));
                var sink = Sink.Ignore<int>().MapMaterializedValue(_ => NotUsed.Instance);

                var broadcast = builder.Add(new Broadcast<int>(2));
                var merge = builder.Add(new Merge<int>(2));

                var f1 = Flow.Create<int>().Select(x => x + 10);
                var f2 = Flow.Create<int>().Select(x => x + 10);
                var f3 = Flow.Create<int>().Select(x => x + 10);
                var f4 = Flow.Create<int>().Select(x => x + 10);

                builder.From(source).Via(f1).Via(broadcast).Via(f2).Via(merge).Via(f3).To(sink);
                builder.From(broadcast).Via(f4).To(merge);

                return ClosedShape.Instance;
            });

            RunnableGraph<NotUsed> rg = RunnableGraph.FromGraph(gdsl);

            rg.Run(_materializer);
        }

        [Fact]
        public async void RunnableGraphMadeOfBackpressuredQueueAndActorRefWithAckWorksAsExpected()
        {
            const int MAX = 4;

            Source<int, ISourceQueueWithComplete<int>> source = Source.Queue<int>(MAX, OverflowStrategy.Backpressure);
            TestProbe probe = CreateTestProbe();
            Sink<IEnumerable<int>, NotUsed> sink = Sink.ActorRefWithAck<IEnumerable<int>>(probe.Ref, "init", "ack", "complete");

            RunnableGraph<ISourceQueueWithComplete<int>> rg = RunnableGraph.FromGraph(GraphDsl.Create(source, sink, Keep.Left,
                (builder, source_, sink_) =>
                {
                    UniformFanOutShape<int, int> broadcaster = builder.Add(new Broadcast<int>(2));
                    UniformFanInShape<IEnumerable<int>, IEnumerable<int>> merger = builder.Add(new Merge<IEnumerable<int>>(2));

                    var f1 = Flow.Create<int>().Aggregate(new List<int>(),
                        (agg, curr) =>
                        {
                            agg.Add(curr);
                            return agg;
                        }).Select(list => list.AsEnumerable());
                    var f2 = Flow.Create<int>().Aggregate(new List<int>(),
                        (agg, curr) =>
                        {
                            agg.Add(curr);
                            return agg;
                        }).Select(list => list.AsEnumerable());

                    builder.From(source_).To(broadcaster.In);
                    builder.From(broadcaster.Out(0)).Via(f1).To(merger.In(0));
                    builder.From(broadcaster.Out(1)).Via(f2).To(merger.In(1));
                    builder.From(merger.Out).To(sink_);

                    return ClosedShape.Instance;
                }));

            ISourceQueueWithComplete<int> q = rg.Run(_materializer);

            probe.ExpectMsg<string>((msg, sender) =>
            {
                if (msg != "init")
                    throw new InvalidOperationException($"Expected: init. Found: {msg}");
                sender.Tell("ack");
            });
            await q.OfferAsync(2);
            await q.OfferAsync(4);
            await q.OfferAsync(8);
            await q.OfferAsync(16);
            q.Complete();
            await q.WatchCompletionAsync();

            probe.ExpectMsg<IEnumerable<int>>((msg, sender) =>
            {
                Assert.Equal(new[] { 2, 4, 8, 16 }.AsEnumerable(), msg);
                sender.Tell("ack");
            });
            probe.ExpectMsg<IEnumerable<int>>((msg, sender) =>
            {
                Assert.Equal(new[] { 2, 4, 8, 16 }.AsEnumerable(), msg);
                sender.Tell("ack");
            });

            probe.ExpectMsg("complete");
            probe.ExpectNoMsg();
        }

        [Fact]
        public async void ElementsShouldBeDroppedAtDivideByZeroEx()
        {
            Streams.Supervision.Decider decider = 
                cause => cause is DivideByZeroException 
                    ? Streams.Supervision.Directive.Resume 
                    : Streams.Supervision.Directive.Stop;

            Flow<int, int, string> flow = Flow.Create<int>()
                .Where(x => 100 / x < 50)
                .Select(x => 100 / (5 - x))
                .MapMaterializedValue(_ => "materialization test")
                .WithAttributes(ActorAttributes.CreateSupervisionStrategy(decider));
            Source<int, float> source = Source.From(Enumerable.Range(0, 6))
                                              .MapMaterializedValue(_ => 2f); // Meaningless mapping just for test
            Sink<int, Task<int>> sink = Sink.Aggregate<int, int>(0, (sum, i) => sum + i);
            IRunnableGraph<(float, string)> materializationTestRunnableGraph = source.Via(flow).ViaMaterialized(flow, Keep.Both).To(sink);
            var rg = source.Via(flow).ToMaterialized(sink, Keep.Right);

            int result = await rg.Run(_materializer);

            Assert.Equal(150, result);
        }

        [Fact]
        public void StreamRecoversOneTimeWithDifferentSource()
        {
            var planB = Source.From(new List<string> { "five", "six", "seven", "eight" });

            Source.From(Enumerable.Range(0, 10)).Select(n =>
                {
                    if (n < 5)
                        return n.ToString();

                    throw new ArithmeticException("Boom!");
                })
                  .RecoverWithRetries(attempts: 1, partialFunc: exception => 
                      exception is ArithmeticException ? planB : null)
                  .RunForeach(Console.WriteLine, _materializer);
        }
    }
}