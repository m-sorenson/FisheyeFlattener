using FisheyeFlattener.Core;
using OpenCvSharp;
using Xunit;

namespace FisheyeFlattener.Tests;

public class TransformTests
{
    [Fact]
    public void FlipHorizontalReversesColumns()
    {
        using var m = new Mat(1, 3, MatType.CV_8UC1);
        m.Set(0, 0, (byte)10);
        m.Set(0, 1, (byte)20);
        m.Set(0, 2, (byte)30);

        Transform.FlipInPlace(m, flipHorizontal: true, flipVertical: false);

        Assert.Equal(30, m.Get<byte>(0, 0));
        Assert.Equal(20, m.Get<byte>(0, 1));
        Assert.Equal(10, m.Get<byte>(0, 2));
    }

    [Fact]
    public void FlipVerticalReversesRows()
    {
        using var m = new Mat(3, 1, MatType.CV_8UC1);
        m.Set(0, 0, (byte)10);
        m.Set(1, 0, (byte)20);
        m.Set(2, 0, (byte)30);

        Transform.FlipInPlace(m, flipHorizontal: false, flipVertical: true);

        Assert.Equal(30, m.Get<byte>(0, 0));
        Assert.Equal(20, m.Get<byte>(1, 0));
        Assert.Equal(10, m.Get<byte>(2, 0));
    }

    [Fact]
    public void FlipTwiceRestoresOriginal()
    {
        using var m = new Mat(1, 4, MatType.CV_8UC1);
        for (int i = 0; i < 4; i++)
            m.Set(0, i, (byte)(i * 10));

        Transform.FlipInPlace(m, true, false);
        Transform.FlipInPlace(m, true, false);

        for (int i = 0; i < 4; i++)
            Assert.Equal(i * 10, m.Get<byte>(0, i));
    }

    [Fact]
    public void TranslateShiftsContentAndFillsBlack()
    {
        using var src = new Mat(4, 4, MatType.CV_8UC1, Scalar.All(0));
        src.Set(0, 0, (byte)255);

        using var dst = Transform.Translate(src, dx: 2, dy: 1);

        Assert.Equal(255, dst.Get<byte>(1, 2));
        Assert.Equal(0, dst.Get<byte>(0, 0));
    }

    [Fact]
    public void MirrorXAndYAreInvolutions()
    {
        double x = 120;
        double y = 80;
        Assert.Equal(x, Transform.MirrorX(Transform.MirrorX(x, 1000), 1000), 6);
        Assert.Equal(y, Transform.MirrorY(Transform.MirrorY(y, 600), 600), 6);
    }
}
