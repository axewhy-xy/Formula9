using System.Linq;
using Formula9.EditorTools;
using Formula9.World;
using NUnit.Framework;

namespace Formula9.Tests
{
    public class BuildingTextureTests
    {
        private const int Size = 128;

        [Test]
        public void AnHdbFacadeHasBodyWallAccentStripesAndUnpaintedGlass()
        {
            var styles = BuildingStyles.Default;
            var painted = FacadePainter.PaintFacade(styles.Styles[styles.IndexOf("hdb_slab")], Size, styles.NormalStrength);
            var masks = Enumerable.Range(0, Size * Size).Select(i => painted.Color[i * 4 + 3]).Distinct().ToList();
            CollectionAssert.IsSubsetOf(new[] { FacadePainter.MaskBody, FacadePainter.MaskAccent, FacadePainter.MaskNone }, masks);
            CollectionAssert.IsSubsetOf(masks, new[] { FacadePainter.MaskBody, FacadePainter.MaskAccent, FacadePainter.MaskNone });
        }

        [Test]
        public void EveryStylePaintsNormalsThatFaceOutOfTheWall()
        {
            var styles = BuildingStyles.Default;
            foreach (var style in styles.Styles)
                foreach (var painted in new[] { FacadePainter.PaintWall(style, Size, styles.NormalStrength), FacadePainter.PaintFacade(style, Size, styles.NormalStrength) })
                {
                    Assert.AreEqual(Size * Size * 4, painted.Normal.Length, style.Name);
                    // z is stored as 0.5 + z / 2, so out of the wall is above the middle
                    Assert.IsTrue(Enumerable.Range(0, Size * Size).All(i => painted.Normal[i * 4 + 2] > 128), style.Name);
                }
        }

        [Test]
        public void TheSameStyleAlwaysPaintsTheSameTexture()
        {
            var styles = BuildingStyles.Default;
            var style = styles.Styles[styles.IndexOf("office")];
            CollectionAssert.AreEqual(FacadePainter.PaintFacade(style, Size, 1).Color, FacadePainter.PaintFacade(style, Size, 1).Color);
        }

        [TestCase("curtain")]
        [TestCase("stripes")]
        [TestCase("checks")]
        [TestCase("print")]
        [TestCase("roller")]
        [TestCase("venetian")]
        [TestCase("dark")]
        public void EveryKindOfCoveringShowsInItsOwnColourBehindTheGlass(string kind)
        {
            var styles = BuildingStyles.Parse("{\"defaultStyle\":\"a\",\"styles\":[{\"name\":\"a\",\"facade\":{\"streaks\":0,\"windows\":{\"curtainChance\":1,\"coverings\":[{\"kind\":\"" + kind
                + "\",\"colors\":[\"#FF0000\"],\"patternColors\":[\"#FF0000\"]}]}}}]}");
            var painted = FacadePainter.PaintFacade(styles.Styles[0], Size, 1);
            // Reddish pixels, with no blue to speak of, are the covering
            var red = Enumerable.Range(0, Size * Size).Count(i => painted.Color[i * 4] > 60 && painted.Color[i * 4] > 3 * painted.Color[i * 4 + 2]);
            Assert.Greater(red, Size * Size / 50, kind);
        }

        [Test]
        public void TheAtlasPutsTheFirstSliceTopLeftAndFillsGapsWithIt()
        {
            byte[] Solid(byte value) => Enumerable.Repeat(value, 2 * 2 * 4).ToArray();
            var (width, height, pixels) = FacadePainter.Atlas(new[] { Solid(1), Solid(2), Solid(3) }, 2, 2);
            Assert.AreEqual((4, 4), (width, height));
            // Rows are stored from the bottom: the top-left pixel is in the last row
            byte At(int x, int yFromTop) => pixels[((height - 1 - yFromTop) * width + x) * 4];
            Assert.AreEqual(1, At(0, 0));
            Assert.AreEqual(2, At(2, 0));
            Assert.AreEqual(3, At(0, 2));
            Assert.AreEqual(1, At(2, 2), "the empty cell repeats the first slice");
        }
    }
}
