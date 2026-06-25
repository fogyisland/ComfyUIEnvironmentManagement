import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

ColumnLayout {
    id: root
    property string label: ""
    property string helperText: ""
    spacing: 4

    Text {
        text: root.label
        font.bold: true
        color: Theme.color("onSurface")
        visible: root.label !== ""
    }

    // dataField 由调用方提供（用 default property 接受单子）
    default property alias dataField: holder.children
    Item {
        id: holder
        Layout.fillWidth: true
        visible: false
    }

    Text {
        text: root.helperText
        font.pixelSize: 11
        color: Theme.color("onSurfaceVariant")
        visible: root.helperText !== ""
    }
}