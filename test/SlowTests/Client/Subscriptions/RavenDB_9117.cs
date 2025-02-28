﻿using System;
using System.Diagnostics;
using System.Threading.Tasks;
using FastTests;
using Raven.Client.Documents.Subscriptions;
using Raven.Client.Exceptions.Documents.Subscriptions;
using Raven.Client.Extensions;
using Raven.Server.Documents.Replication;
using Raven.Server.ServerWide.Context;
using Raven.Tests.Core.Utils.Entities;
using Sparrow.Server;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Client.Subscriptions
{
    public class RavenDB_9117 : RavenTestBase
    {
        public RavenDB_9117(ITestOutputHelper output) : base(output)
        {
        }

        private readonly TimeSpan _reasonableWaitTime = Debugger.IsAttached ? TimeSpan.FromSeconds(60 * 10) : TimeSpan.FromSeconds(6);

        [Fact]
        public async Task SubscriptionWorkerRetryEventGivesCorrectError()
        {
            using (var store = GetDocumentStore())
            {
                string cvFirst = null;
                using (var session = store.OpenAsyncSession())
                {
                    User newUser = new User();
                    await session.StoreAsync(newUser);
                    await session.SaveChangesAsync();
                    var metadata = session.Advanced.GetMetadataFor(newUser);
                    cvFirst = (string)metadata[Raven.Client.Constants.Documents.Metadata.ChangeVector];
                }

                var firstItemchangeVector = cvFirst.ToChangeVector();
                firstItemchangeVector[0].Etag += 10;
                string cvBigger = firstItemchangeVector.SerializeVector();

                var sn = store.Subscriptions.Create<User>();
                var worker = store.Subscriptions.GetSubscriptionWorker<User>(new SubscriptionWorkerOptions(sn)
                {
                    TimeToWaitBeforeConnectionRetry = TimeSpan.FromMilliseconds(100)
                });
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var signalWhenForcefullyUpdatedCV = new AsyncManualResetEvent();
                var signalWhenStartedProcessingDoc = new AsyncManualResetEvent();

                worker.OnSubscriptionConnectionRetry += e =>
                {
                    tcs.SetException(e);
                };
                _ = worker.Run(async x =>
                    {
                        signalWhenStartedProcessingDoc.Set();

                        Assert.True(await signalWhenForcefullyUpdatedCV.WaitAsync(TimeSpan.FromSeconds(60)));
                    });

                Assert.True(await signalWhenStartedProcessingDoc.WaitAsync(_reasonableWaitTime));
                var database = await GetDatabase(store.Database);

                SubscriptionState subscriptionState;
                using (database.ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
                using (context.OpenReadTransaction())
                {
                    subscriptionState = database.SubscriptionStorage.GetSubscriptionFromServerStore(context, sn);
                }

                var index = database.SubscriptionStorage.PutSubscription(new SubscriptionCreationOptions()
                {
                    ChangeVector = cvBigger,
                    Name = subscriptionState.SubscriptionName,
                    Query = subscriptionState.Query
                }, Guid.NewGuid().ToString(), subscriptionState.SubscriptionId, false);

                await index.WaitWithTimeout(_reasonableWaitTime);

                await database.RachisLogIndexNotifications.WaitForIndexNotification(index.Result, database.ServerStore.Engine.OperationTimeout).WaitWithTimeout(_reasonableWaitTime);
                signalWhenForcefullyUpdatedCV.Set();
                Assert.True(await Assert.ThrowsAsync<SubscriptionChangeVectorUpdateConcurrencyException>(() => tcs.Task).WaitWithTimeout(_reasonableWaitTime));
            }
        }
    }
}
