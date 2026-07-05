pragma Singleton
import QtQuick

QtObject {
    readonly property var light: ({
        "primary":          "#6750A4",
        "onPrimary":        "#FFFFFF",
        "primaryContainer": "#EADDFF",
        "onPrimaryContainer":"#21005D",
        "secondary":        "#625B71",
        "background":       "#FFFBFE",
        "onBackground":     "#1C1B1F",
        "surface":          "#FFFBFE",
        "surfaceVariant":   "#E7E0EC",
        "onSurface":        "#1C1B1F",
        "onSurfaceVariant": "#49454F",
        "error":            "#B3261E",
        "outline":          "#79747E",
    })
    readonly property var dark: ({
        "primary":          "#D0BCFF",
        "onPrimary":        "#381E72",
        "primaryContainer": "#4F378B",
        "onPrimaryContainer":"#EADDFF",
        "secondary":        "#CCC2DC",
        "background":       "#1C1B1F",
        "onBackground":     "#E6E1E5",
        "surface":          "#1C1B1F",
        "surfaceVariant":   "#49454F",
        "onSurface":        "#E6E1E5",
        "onSurfaceVariant": "#CAC4D0",
        "error":            "#F2B8B5",
        "outline":          "#938F99",
    })

    // mode: "light" / "dark" / "system"
    property string mode: "system"

    function effectiveMode() {
        if (mode !== "system") return mode
        if (typeof Qt !== "undefined" && Qt.styleHints && Qt.styleHints.colorScheme !== undefined) {
            return Qt.styleHints.colorScheme === Qt.ColorScheme.Dark ? "dark" : "light"
        }
        return "light"
    }

    function color(name) {
        const m = effectiveMode()
        const palette = m === "dark" ? dark : light
        return palette[name] || "#000000"
    }

    readonly property int spacing: 8
    readonly property int spacingLarge: 16
    readonly property int radius: 12
}
