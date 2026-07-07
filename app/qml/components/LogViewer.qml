import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import Manager 1.0

Rectangle {
    id: root
    property var logLines: []
    property int maxLines: 500

    color: Theme.color("surface")
    border.color: Theme.color("outline")
    border.width: 1
    radius: 4

    ScrollView {
        anchors.fill: parent
        anchors.margins: 8
        clip: true

        TextArea {
            id: logArea
            readOnly: true
            wrapMode: TextArea.NoWrap
            text: root.logLines.join("\n")
            color: Theme.color("onSurface")
            background: null
            font.family: "Cascadia Mono, Consolas, monospace"
            font.pixelSize: 12
            selectByMouse: true
        }
    }
}
