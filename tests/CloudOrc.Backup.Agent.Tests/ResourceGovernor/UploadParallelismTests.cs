using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Backup.Agent.ResourceGovernor;

namespace CloudOrc.Backup.Agent.Tests.ResourceGovernor;

/// <summary>Uploads are network-bound: more concurrent uploads than CPU/disk workers, still capped by a lease.</summary>
public class UploadParallelismTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void UploadsAreDecoupledFromCpuParallelism()
    {
        var balanced = ResourceProfiles.Resolve(ResourceProfileNames.Balanced, 4, 8 * Gb);
        Assert.Equal(2, balanced.Parallelism);
        Assert.Equal(8, balanced.UploadParallelism);
        Assert.Equal(2, balanced.GoMaxProcs); // CPU stays bounded

        Assert.Equal(2, ResourceProfiles.Resolve(ResourceProfileNames.Eco, 2, 4 * Gb).UploadParallelism);
        Assert.Equal(16, ResourceProfiles.Resolve(ResourceProfileNames.Throughput, 8, 16 * Gb).UploadParallelism);
    }

    [Fact]
    public void LeaseWithoutParallelLimitKeepsUploads()
    {
        var budget = ResourceProfiles.ApplyLease(
            ResourceProfiles.Resolve(ResourceProfileNames.Balanced, 4, 8 * Gb),
            new AdmissionLease { LeaseId = "l", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10), FencingToken = 1 });
        Assert.Equal(8, budget.UploadParallelism);
    }

    [Fact]
    public void LeaseParallelLimitAlsoCapsUploads()
    {
        var budget = ResourceProfiles.ApplyLease(
            ResourceProfiles.Resolve(ResourceProfileNames.Balanced, 4, 8 * Gb),
            new AdmissionLease { LeaseId = "l", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10), FencingToken = 1, MaxParallel = 1 });
        Assert.Equal(1, budget.Parallelism);
        Assert.Equal(1, budget.UploadParallelism);
    }
}
