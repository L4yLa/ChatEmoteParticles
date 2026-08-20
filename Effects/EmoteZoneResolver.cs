using System;
using ChatEmoteParticles.Configuration;

namespace ChatEmoteParticles.Effects
{
    /// <summary>矩形ゾーン（XY 平面）。</summary>
    internal struct ZoneRect
    {
        internal float XMin;
        internal float XMax;
        internal float YMin;
        internal float YMax;

        internal float PickX(System.Random rng) => XMin + (float)rng.NextDouble() * (XMax - XMin);
        internal float PickY(System.Random rng) => YMin + (float)rng.NextDouble() * (YMax - YMin);
    }

    /// <summary>
    /// 設定値からゾーン矩形を組み立て、面積比に応じて発生位置を抽選する（SPEC §7）。
    /// 固定比率は持たず、設定変更のたびに面積から重みを再計算する。
    /// </summary>
    internal sealed class EmoteZoneResolver
    {
        private ZoneRect _above;
        private ZoneRect _left;
        private ZoneRect _right;
        private EmoteZone _zone = EmoteZone.Both;
        private float _aboveWeight = 0.5f;

        private float _songZ = 16f;
        private float _menuZ = 5f;

        internal void Refresh(PluginConfig config)
        {
            if (config == null) return;

            _zone = config.Zone;
            _aboveWeight = config.AboveLaneWeight;
            _songZ = config.EmoteZ;
            _menuZ = config.MenuEmoteZ;

            var halfX = Math.Abs(config.AboveLaneHalfX);
            _above = new ZoneRect
            {
                XMin = -halfX,
                XMax = halfX,
                YMin = Math.Min(config.AboveLaneMinY, config.AboveLaneMaxY),
                YMax = Math.Max(config.AboveLaneMinY, config.AboveLaneMaxY),
            };

            var innerX = Math.Abs(config.SideInnerX);
            var outerX = Math.Abs(config.SideOuterX);
            if (innerX > outerX)
            {
                var tmp = innerX;
                innerX = outerX;
                outerX = tmp;
            }

            var sideYMin = Math.Min(config.SideMinY, config.SideMaxY);
            var sideYMax = Math.Max(config.SideMinY, config.SideMaxY);

            _right = new ZoneRect { XMin = innerX, XMax = outerX, YMin = sideYMin, YMax = sideYMax };
            _left = new ZoneRect { XMin = -outerX, XMax = -innerX, YMin = sideYMin, YMax = sideYMax };
        }

        /// <summary>現在のシーンに応じた Z 座標。</summary>
        internal float GetZ(bool inSong) => inSong ? _songZ : _menuZ;

        /// <summary>面積比に基づいてゾーンを1つ抽選する。左右ゾーンは 1:1 で選ぶ。</summary>
        internal ZoneRect Pick(System.Random rng)
        {
            switch (_zone)
            {
                case EmoteZone.AboveLane:
                    return _above;

                case EmoteZone.Sides:
                    return rng.NextDouble() < 0.5 ? _left : _right;

                default: // Both
                    if (rng.NextDouble() < _aboveWeight) return _above;
                    return rng.NextDouble() < 0.5 ? _left : _right;
            }
        }
    }
}
