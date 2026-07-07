import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import Manager 1.0

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

    // dataField 由调用方提供（用 default property 接受单子）。
    // 注意:之前用 `alias dataField: holder.children` + holder visible:false,
    // 会把输入控件吞到不可见 Item 里,导致 SettingsPage 全部设置项只剩 label,
    // 看不到 ComboBox/PathField。现把 holder 设为可见,只作为 column 容器。
    default property alias dataField: holder.children
    ColumnLayout {
        id: holder
        Layout.fillWidth: true
        spacing: 8
    }

    Text {
        text: root.helperText
        font.pixelSize: 11
        color: Theme.color("onSurfaceVariant")
        visible: root.helperText !== ""
    }
}