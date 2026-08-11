package com.starry.clipboardtool.ui.theme

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.path
import androidx.compose.ui.unit.dp

/** 剪贴板（粘贴纸）图标：Material ContentPaste 路径，icons-core 不含此图标，手绘兜底。 */
val ContentPasteIcon: ImageVector by lazy {
    ImageVector.Builder(
        name = "ContentPaste", defaultWidth = 24.dp, defaultHeight = 24.dp,
        viewportWidth = 24f, viewportHeight = 24f).apply {
        path(fill = SolidColor(Color.Black)) {
            moveTo(19f, 2f)
            horizontalLineToRelative(-4.18f)
            curveTo(14.4f, 0.84f, 13.3f, 0f, 12f, 0f)
            curveTo(10.7f, 0f, 9.6f, 0.84f, 9.18f, 2f)
            horizontalLineTo(5f)
            curveTo(3.9f, 2f, 3f, 2.9f, 3f, 4f)
            verticalLineToRelative(16f)
            curveTo(3f, 21.1f, 3.9f, 22f, 5f, 22f)
            horizontalLineToRelative(14f)
            curveTo(20.1f, 22f, 21f, 21.1f, 21f, 20f)
            verticalLineTo(4f)
            curveTo(21f, 2.9f, 20.1f, 2f, 19f, 2f)
            close()
            moveTo(12f, 2f)
            curveTo(12.55f, 2f, 13f, 2.45f, 13f, 3f)
            reflectiveCurveToRelative(-0.45f, 1f, -1f, 1f)
            reflectiveCurveToRelative(-1f, -0.45f, -1f, -1f)
            reflectiveCurveToRelative(0.45f, -1f, 1f, -1f)
            close()
            moveTo(19f, 20f)
            horizontalLineTo(5f)
            verticalLineTo(4f)
            horizontalLineToRelative(2f)
            verticalLineToRelative(3f)
            horizontalLineToRelative(10f)
            verticalLineTo(4f)
            horizontalLineToRelative(2f)
            verticalLineToRelative(16f)
            close()
        }
    }.build()
}
