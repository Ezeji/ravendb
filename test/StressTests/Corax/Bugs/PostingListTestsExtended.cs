using System;
using System.Collections.Generic;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace StressTests.Corax.Bugs;

public class PostingListTestsExtended : NoDisposalNoOutputNeeded
{
    public PostingListTestsExtended(ITestOutputHelper output) : base(output)
    {
    }

    public static IEnumerable<object[]> Configuration =>
        new List<object[]>
        {
            new object[] {Random.Shared.Next(), Random.Shared.Next(20000000)},
            new object[] {Random.Shared.Next(), Random.Shared.Next(2000000)},
            new object[] {Random.Shared.Next(), Random.Shared.Next(200000)},
            new object[] {Random.Shared.Next(), Random.Shared.Next(20000)},
        };

    [RavenTheory(RavenTestCategory.Voron | RavenTestCategory.Corax)]
    [InlineData(1337, 200000)]
    [InlineData(1064156071, 796)]
    [InlineData(511767612, 4172)]
    [InlineData(439188321, 502627)]
    [InlineData(506431817, 2)]
    [InlineData(1477187726, 1828658)]
    [MemberData("Configuration")]
    public void CanDeleteAndInsertInRandomOrder(int seed, int size)
    {
        using var testClass = new FastTests.Voron.PostingLists.PostingListTests(Output);
        testClass.CanDeleteAndInsertInRandomOrder(seed, size, 10);
    }

    [MultiplatformTheory(RavenPlatform.Windows)]
    [InlineData(391060845, 31707323)]
    public void CanDeleteAndInsertInRandomOrderWindows(int seed, int size)
    {
        using var testClass = new FastTests.Voron.PostingLists.PostingListTests(Output);
        testClass.CanDeleteAndInsertInRandomOrder(seed, size, 10);
    }
}
