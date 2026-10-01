using DSPiConsole.Core.Firmware;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Core.Tests;

/// <summary>The firmware installer's state machine, the bundled-image lookup
/// and the reboot rule, with no hardware.</summary>
public sealed class FirmwareInstallerTests : IDisposable
{
    private sealed class FakeLocator : IBootloaderLocator
    {
        public IReadOnlyList<BootloaderBoard> CurrentBoards { get; set; } = Array.Empty<BootloaderBoard>();
        public event Action<IReadOnlyList<BootloaderBoard>>? Changed;
        public void Start() { }
        public void Stop() { }
        public void Emit(params BootloaderBoard[] boards) { CurrentBoards = boards; Changed?.Invoke(boards); }
    }

    private sealed class FakeVerifier(FirmwareVersion? reply) : IFirmwareVerifier
    {
        public Task<FirmwareVersion?> AwaitDeviceVersionAsync(TimeSpan timeout, CancellationToken cancel) => Task.FromResult(reply);
    }

    private static readonly FirmwareVersion Version = new(1, 1, 6, 4);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dspi-fw-" + Guid.NewGuid().ToString("N"));
    private readonly string _drive;
    private readonly string _image;
    private DateTime _now = new(2026, 1, 1);

    public FirmwareInstallerTests()
    {
        Directory.CreateDirectory(_dir);
        _drive = Directory.CreateDirectory(Path.Combine(_dir, "drive")).FullName;
        _image = Path.Combine(_dir, "DSPi-RP2350-v1.1.6-beta4.uf2");
        File.WriteAllBytes(_image, Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray());
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private (FirmwareInstaller Installer, FakeLocator Locator) Make(FirmwareVersion? reply = null,
        Func<BootloaderChip, FirmwareImage>? images = null)
    {
        var locator = new FakeLocator();
        var installer = new FirmwareInstaller(locator, new FakeVerifier(reply ?? Version),
            images ?? (chip => new FirmwareImage(chip, _image, Version)), a => a(), () => _now);
        installer.BeginWatching();
        return (installer, locator);
    }

    private BootloaderBoard Board(string? drive = "") => new(BootloaderChip.RP2350, drive == "" ? _drive : drive);

    [Fact]
    public void DetectionFollowsWhatIsPlugged()
    {
        var (installer, locator) = Make();
        Assert.IsType<FirmwareInstallState.WaitingForBoard>(installer.State);
        locator.Emit(Board());
        Assert.IsType<FirmwareInstallState.Ready>(installer.State);
        locator.Emit(Board(), Board());
        Assert.Equal(FirmwareInstallErrorKind.MultipleBoards, Assert.IsType<FirmwareInstallState.Failed>(installer.State).Error.Kind);
        // A detection failure gives way when what is plugged changes.
        locator.Emit();
        Assert.IsType<FirmwareInstallState.WaitingForBoard>(installer.State);
    }

    [Fact]
    public void DriveGetsTimeToAppear()
    {
        var (installer, locator) = Make();
        locator.Emit(Board(null));
        Assert.IsType<FirmwareInstallState.WaitingForVolume>(installer.State);
        _now += TimeSpan.FromSeconds(5);
        locator.Emit(Board(null));
        Assert.IsType<FirmwareInstallState.WaitingForVolume>(installer.State);
        _now += TimeSpan.FromSeconds(4);
        locator.Emit(Board(null));
        Assert.Equal(FirmwareInstallErrorKind.VolumeNotMounted, Assert.IsType<FirmwareInstallState.Failed>(installer.State).Error.Kind);
        // A late drive still recovers before an install has begun.
        locator.Emit(Board());
        Assert.IsType<FirmwareInstallState.Ready>(installer.State);
    }

    [Fact]
    public async Task ArmedBeforeTheBoardArrivesWritesAndVerifies()
    {
        var (installer, locator) = Make();
        installer.InstallWhenReady();
        Assert.True(installer.IsArmed);
        locator.Emit(Board());
        Assert.False(installer.IsArmed);
        await installer.CurrentRun!;
        Assert.Equal(Version, Assert.IsType<FirmwareInstallState.Verified>(installer.State).Version);
        Assert.Equal(File.ReadAllBytes(_image), File.ReadAllBytes(Path.Combine(_drive, Path.GetFileName(_image))));
        // The outcome holds against later scans.
        locator.Emit();
        Assert.IsType<FirmwareInstallState.Verified>(installer.State);
    }

    [Fact]
    public async Task AReadyBoardIsWrittenOnlyWhenTheUserCommits()
    {
        var (installer, locator) = Make();
        locator.Emit(Board());
        Assert.IsType<FirmwareInstallState.Ready>(installer.State);
        Assert.Null(installer.CurrentRun);
        installer.InstallWhenReady();
        await installer.CurrentRun!;
        Assert.IsType<FirmwareInstallState.Verified>(installer.State);
    }

    [Fact]
    public async Task WrongVersionOrNoDeviceFails()
    {
        var (installer, locator) = Make(new FirmwareVersion(1, 1, 5));
        locator.Emit(Board());
        installer.InstallWhenReady();
        await installer.CurrentRun!;
        Assert.Equal(FirmwareInstallErrorKind.VersionMismatch, Assert.IsType<FirmwareInstallState.Failed>(installer.State).Error.Kind);

        var locator2 = new FakeLocator();
        var silent = new FirmwareInstaller(locator2, new FakeVerifier(null),
            chip => new FirmwareImage(chip, _image, Version), a => a(), () => _now);
        silent.BeginWatching();
        locator2.Emit(Board());
        silent.InstallWhenReady();
        await silent.CurrentRun!;
        Assert.Equal(FirmwareInstallErrorKind.DeviceDidNotReturn, Assert.IsType<FirmwareInstallState.Failed>(silent.State).Error.Kind);
    }

    [Fact]
    public void AStaleImageFailsAndStaysFailedUntilReset()
    {
        var (installer, locator) = Make(images: _ => throw new FirmwareInstallException(FirmwareInstallError.ImageStale("1.1.5", "1.1.6 beta 4")));
        locator.Emit(Board());
        installer.InstallWhenReady();
        Assert.Equal(FirmwareInstallErrorKind.ImageStale, Assert.IsType<FirmwareInstallState.Failed>(installer.State).Error.Kind);
        locator.Emit(Board());
        Assert.IsType<FirmwareInstallState.Failed>(installer.State);
        // Try Again returns to detection with nothing armed.
        installer.Reset();
        Assert.IsType<FirmwareInstallState.Ready>(installer.State);
        Assert.False(installer.IsArmed);
    }

    [Theory]
    [InlineData(0.96, true, true)]
    [InlineData(0.5, false, true)]
    [InlineData(0.5, true, false)]
    public void RebootSignal(double fraction, bool drivePresent, bool reboot) =>
        Assert.Equal(reboot, FirmwareInstaller.IsRebootSignal(fraction, drivePresent));

    [Fact]
    public void BundledImageMustMatchTheApp()
    {
        Assert.Equal(new FirmwareVersion(1, 1, 7), FirmwareImage.VersionFromAssetName("DSPi-RP2350-v1.1.7.uf2"));
        Assert.Equal(Version, FirmwareImage.VersionFromAssetName("DSPi-RP2040-v1.1.6-beta4.uf2"));

        var image = FirmwareImage.Bundled(BootloaderChip.RP2350, _dir, Version);
        Assert.Equal(_image, image.Path);
        var stale = Assert.Throws<FirmwareInstallException>(() => FirmwareImage.Bundled(BootloaderChip.RP2350, _dir, new FirmwareVersion(1, 1, 7)));
        Assert.Equal(FirmwareInstallErrorKind.ImageStale, stale.Error.Kind);
        var missing = Assert.Throws<FirmwareInstallException>(() => FirmwareImage.Bundled(BootloaderChip.RP2040, _dir, Version));
        Assert.Equal(FirmwareInstallErrorKind.ImageMissing, missing.Error.Kind);
    }

    [Fact]
    public void AnOlderImageBesideTheCurrentOneIsPassedOver()
    {
        // "-beta3" sorts before "-beta4", so the old file is listed first.
        File.WriteAllBytes(Path.Combine(_dir, "DSPi-RP2350-v1.1.6-beta3.uf2"), new byte[16]);
        File.WriteAllBytes(Path.Combine(_dir, "DSPi-RP2350-v1.1.5.uf2"), new byte[16]);
        var image = FirmwareImage.Bundled(BootloaderChip.RP2350, _dir, Version);
        Assert.Equal(_image, image.Path);
        Assert.Equal(Version, image.Version);
    }
}
