// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.AspNetCore.SignalR.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.AspNetCore.SignalR.Client.Tests
{
    public partial class HubConnectionTests : VerifiableLoggedTest
    {
        public HubConnectionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact]
        public async Task InvokeThrowsIfSerializingMessageFails()
        {
            var exception = new InvalidOperationException();
            var hubConnection = CreateHubConnection(new TestConnection(), protocol: MockHubProtocol.Throw(exception));
            await hubConnection.StartAsync().OrTimeout();

            var actualException =
                await Assert.ThrowsAsync<InvalidOperationException>(async () => await hubConnection.InvokeAsync<int>("test").OrTimeout());
            Assert.Same(exception, actualException);
        }

        [Fact]
        public async Task SendAsyncThrowsIfSerializingMessageFails()
        {
            var exception = new InvalidOperationException();
            var hubConnection = CreateHubConnection(new TestConnection(), protocol: MockHubProtocol.Throw(exception));
            await hubConnection.StartAsync().OrTimeout();

            var actualException =
                await Assert.ThrowsAsync<InvalidOperationException>(async () => await hubConnection.SendAsync("test").OrTimeout());
            Assert.Same(exception, actualException);
        }

        [Fact]
        public async Task ClosedEventRaisedWhenTheClientIsStopped()
        {
            var builder = new HubConnectionBuilder();

            var delegateConnectionFactory = new DelegateConnectionFactory(
                format => new TestConnection().StartAsync(format),
                connection => ((TestConnection)connection).DisposeAsync());
            builder.Services.AddSingleton<IConnectionFactory>(delegateConnectionFactory);

            var hubConnection = builder.Build();
            var closedEventTcs = new TaskCompletionSource<Exception>();
            hubConnection.Closed += e =>
            {
                closedEventTcs.SetResult(e);
                return Task.CompletedTask;
            };

            await hubConnection.StartAsync().OrTimeout();
            await hubConnection.StopAsync().OrTimeout();
            Assert.Null(await closedEventTcs.Task);
        }

        [Fact]
        public async Task PendingInvocationsAreCanceledWhenConnectionClosesCleanly()
        {
            var hubConnection = CreateHubConnection(new TestConnection());

            await hubConnection.StartAsync().OrTimeout();
            var invokeTask = hubConnection.InvokeAsync<int>("testMethod").OrTimeout();
            await hubConnection.StopAsync().OrTimeout();

            await Assert.ThrowsAsync<TaskCanceledException>(async () => await invokeTask);
        }

        [Fact]
        public async Task PendingInvocationsAreTerminatedWithExceptionWhenTransportCompletesWithError()
        {
            var connection = new TestConnection();
            var hubConnection = CreateHubConnection(connection, protocol: Mock.Of<IHubProtocol>());

            await hubConnection.StartAsync().OrTimeout();
            var invokeTask = hubConnection.InvokeAsync<int>("testMethod").OrTimeout();

            var exception = new InvalidOperationException();
            connection.CompleteFromTransport(exception);

            var actualException = await Assert.ThrowsAsync<InvalidOperationException>(async () => await invokeTask);
            Assert.Equal(exception, actualException);
        }

        [Fact]
        public async Task ConnectionTerminatedIfServerTimeoutIntervalElapsesWithNoMessages()
        {
            var hubConnection = CreateHubConnection(new TestConnection());
            hubConnection.ServerTimeout = TimeSpan.FromMilliseconds(100);

            var closeTcs = new TaskCompletionSource<Exception>();
            hubConnection.Closed += ex =>
            {
                closeTcs.TrySetResult(ex);
                return Task.CompletedTask;
            };

            await hubConnection.StartAsync().OrTimeout();

            var exception = Assert.IsType<TimeoutException>(await closeTcs.Task.OrTimeout());

            // We use an interpolated string so the tests are accurate on non-US machines.
            Assert.Equal($"Server timeout ({hubConnection.ServerTimeout.TotalMilliseconds:0.00}ms) elapsed without receiving a message from the server.", exception.Message);
        }

        [Fact]
        public async Task PendingInvocationsAreTerminatedIfServerTimeoutIntervalElapsesWithNoMessages()
        {
            bool ExpectedErrors(WriteContext writeContext)
            {
                return writeContext.LoggerName == typeof(HubConnection).FullName &&
                       writeContext.EventId.Name == "ShutdownWithError";
            }

            using (StartVerifiableLog(out var loggerFactory, LogLevel.Trace, expectedErrorsFilter: ExpectedErrors))
            {
                var hubConnection = CreateHubConnection(new TestConnection(), loggerFactory: loggerFactory);
                hubConnection.ServerTimeout = TimeSpan.FromMilliseconds(2000);

                await hubConnection.StartAsync().OrTimeout();

                // Start an invocation (but we won't complete it)
                var invokeTask = hubConnection.InvokeAsync("Method").OrTimeout();

                var exception = await Assert.ThrowsAsync<TimeoutException>(() => invokeTask);

                // We use an interpolated string so the tests are accurate on non-US machines.
                Assert.Equal($"Server timeout ({hubConnection.ServerTimeout.TotalMilliseconds:0.00}ms) elapsed without receiving a message from the server.", exception.Message);
            }
        }

        [Fact]
        public async Task StreamIntsToServer()
        {
            using (StartVerifiableLog(out var loggerFactory, LogLevel.Trace))
            {
                var connection = new TestConnection();
                var hubConnection = CreateHubConnection(connection, loggerFactory: loggerFactory);
                await hubConnection.StartAsync().OrTimeout();

                var channel = Channel.CreateUnbounded<int>();
                var invokeTask = hubConnection.InvokeAsync<int>("SomeMethod", channel.Reader);

                var invocation = await connection.ReadSentJsonAsync();
                Assert.Equal(HubProtocolConstants.InvocationMessageType, invocation["type"]);
                Assert.Equal("SomeMethod", invocation["target"]);
                Assert.True((bool)invocation["hasStream"]);
                var streamId = invocation["arguments"][0]["streamId"];

                foreach (var number in new[] { 42, 43, 322, 3145, -1234 })
                {
                    await channel.Writer.WriteAsync(number);

                    var item = await connection.ReadSentJsonAsync();
                    Assert.Equal(HubProtocolConstants.StreamItemMessageType, item["type"]);
                    Assert.Equal(number, item["item"]);
                    Assert.Equal(streamId, item["invocationId"]); // I realize this is poorly named, TODO change
                }

                channel.Writer.TryComplete();
                var completion = await connection.ReadSentJsonAsync();
                Assert.Equal(HubProtocolConstants.StreamCompleteMessageType, completion["type"]);

                await connection.ReceiveJsonMessage(new { type = HubProtocolConstants.CompletionMessageType, invocationId = invocation["invocationId"], result = 42 });
                var result = await invokeTask;
                Assert.Equal(42, result);
            }
        }

        [Fact]
        public async Task StreamIntsToServerViaSend()
        {
            using (StartVerifiableLog(out var loggerFactory, LogLevel.Trace))
            {
                var connection = new TestConnection();
                var hubConnection = CreateHubConnection(connection, loggerFactory: loggerFactory);
                await hubConnection.StartAsync().OrTimeout();

                var channel = Channel.CreateUnbounded<int>();
                var sendTask = hubConnection.SendAsync("SomeMethod", channel.Reader);

                var invocation = await connection.ReadSentJsonAsync();
                Assert.Equal(HubProtocolConstants.InvocationMessageType, invocation["type"]);
                Assert.Equal("SomeMethod", invocation["target"]);
                Assert.True((bool)invocation["hasStream"]);
                Assert.Null(invocation["invocationId"]);
            }
        }

        [Fact]
        public async Task StreamsObjectsToServer()
        {
            using (StartVerifiableLog(out var loggerFactory, LogLevel.Trace))
            {
                var connection = new TestConnection();
                var hubConnection = CreateHubConnection(connection, loggerFactory: loggerFactory);
                await hubConnection.StartAsync().OrTimeout();

                var channel = Channel.CreateUnbounded<object>();
                var invokeTask = hubConnection.InvokeAsync<SampleObject>("UploadMethod", channel.Reader);

                var invocation = await connection.ReadSentJsonAsync();
                Assert.Equal(HubProtocolConstants.InvocationMessageType, invocation["type"]);
                Assert.Equal("UploadMethod", invocation["target"]);
                Assert.True((bool)invocation["hasStream"]);
                var id = invocation["invocationId"];

                var items = new[] { new SampleObject("ab", 12), new SampleObject("ef", 23) };
                foreach (var item in items)
                {
                    await channel.Writer.WriteAsync(item);

                    var received = await connection.ReadSentJsonAsync();
                    Assert.Equal(HubProtocolConstants.StreamItemMessageType, received["type"]);
                    Assert.Equal(item.Foo, received["item"]["foo"]);
                    Assert.Equal(item.Bar, received["item"]["bar"]);
                }

                channel.Writer.TryComplete();
                var completion = await connection.ReadSentJsonAsync();
                Assert.Equal(HubProtocolConstants.StreamCompleteMessageType, completion["type"]);

                var expected = new SampleObject("oof", 14);
                await connection.ReceiveJsonMessage(new { type = HubProtocolConstants.CompletionMessageType, invocationId = id, result = expected });
                var result = await invokeTask;

                Assert.Equal(expected.Foo, result.Foo);
                Assert.Equal(expected.Bar, result.Bar);
            }
        }

        [Fact]
        public async Task UploadStreamCancelled()
        {
            using (StartVerifiableLog(out var loggerFactory, LogLevel.Trace))
            {
                var connection = new TestConnection();
                var hubConnection = CreateHubConnection(connection, loggerFactory: loggerFactory);
                await hubConnection.StartAsync().OrTimeout();

                var cts = new CancellationTokenSource();
                var channel = Channel.CreateUnbounded<int>();
                var invokeTask = hubConnection.InvokeAsync<object>("UploadMethod", channel.Reader, cts.Token);

                var invokeMessage = await connection.ReadSentJsonAsync();
                Assert.Equal(HubProtocolConstants.InvocationMessageType, invokeMessage["type"]);

                cts.Cancel();

                // after cancellation, don't send from the pipe
                foreach (var number in new[] { 42, 43, 322, 3145, -1234 })
                {
                    await channel.Writer.WriteAsync(number);
                }

                // the next sent message should be a completion message
                var complete = await connection.ReadSentJsonAsync();
                Assert.Equal(HubProtocolConstants.StreamCompleteMessageType, complete["type"]);
                Assert.NotNull(complete["error"]);
            } 
        }

        [Fact]
        public async Task PrematureInvocationResponse()
        {
            using (StartVerifiableLog(out var loggerFactory, LogLevel.Trace))
            {
                var connection = new TestConnection();
                var hubConnection = CreateHubConnection(connection, loggerFactory: loggerFactory);
                await hubConnection.StartAsync().OrTimeout();

                var channel = Channel.CreateUnbounded<int>();
                var invokeTask = hubConnection.InvokeAsync<object>("UploadMethod", channel.Reader);
                var invocation = await connection.ReadSentJsonAsync();
                Assert.Equal(HubProtocolConstants.InvocationMessageType, invocation["type"]);
                var id = invocation["invocationId"];

                await connection.ReceiveJsonMessage(new { type = HubProtocolConstants.CompletionMessageType, invocationId = id, result = 10 });

                var result = await invokeTask;
                Assert.Equal(10L, result);

                // after the server returns, with whatever response
                // the client's behavior is undefined, and the server is responsible for ignoring stray messages
            }
        }

        [Fact]
        public async Task WrongTypeOnServerResponse()
        {
            using (StartVerifiableLog(out var loggerFactory, LogLevel.Trace))
            {
                var connection = new TestConnection();
                var hubConnection = CreateHubConnection(connection, loggerFactory: loggerFactory);
                await hubConnection.StartAsync().OrTimeout();

                // we expect to get send ints, and receive an int back
                var channel = Channel.CreateUnbounded<int>();
                var invokeTask = hubConnection.InvokeAsync<int>("SumInts", channel.Reader);

                var invocation = await connection.ReadSentJsonAsync();
                Assert.Equal(HubProtocolConstants.InvocationMessageType, invocation["type"]);
                var id = invocation["invocationId"];

                await channel.Writer.WriteAsync(5);
                await channel.Writer.WriteAsync(10);

                await connection.ReceiveJsonMessage(new { type = HubProtocolConstants.CompletionMessageType, invocationId = id, result = "humbug" });

                await Assert.ThrowsAsync<Newtonsoft.Json.JsonSerializationException>(async () => await invokeTask);
            }
        }

        private class SampleObject
        {
            public SampleObject(string foo, int bar)
            {
                Foo = foo;
                Bar = bar;
            }

            public string Foo { get; private set; }
            public int Bar { get; private set; }
        }


        // Moq really doesn't handle out parameters well, so to make these tests work I added a manual mock -anurse
        private class MockHubProtocol : IHubProtocol
        {
            private HubInvocationMessage _parsed;
            private Exception _error;

            public static MockHubProtocol ReturnOnParse(HubInvocationMessage parsed)
            {
                return new MockHubProtocol
                {
                    _parsed = parsed
                };
            }

            public static MockHubProtocol Throw(Exception error)
            {
                return new MockHubProtocol
                {
                    _error = error
                };
            }

            public string Name => "MockHubProtocol";
            public int Version => 1;
            public int MinorVersion => 1;

            public TransferFormat TransferFormat => TransferFormat.Binary;

            public bool IsVersionSupported(int version)
            {
                return true;
            }

            public bool TryParseMessage(ref ReadOnlySequence<byte> input, IInvocationBinder binder, out HubMessage message)
            {
                if (_error != null)
                {
                    throw _error;
                }
                if (_parsed != null)
                {
                    message = _parsed;
                    return true;
                }

                throw new InvalidOperationException("No Parsed Message provided");
            }

            public void WriteMessage(HubMessage message, IBufferWriter<byte> output)
            {
                if (_error != null)
                {
                    throw _error;
                }
            }

            public ReadOnlyMemory<byte> GetMessageBytes(HubMessage message)
            {
                return HubProtocolExtensions.GetMessageBytes(this, message);
            }
        }
    }
}
