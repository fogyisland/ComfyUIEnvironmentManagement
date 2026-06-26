import QtQuick 2.15
import QtQuick.Controls 2.15
import QtQuick.Layouts 1.15

Rectangle {
    id: root
    property var node: ({})
    property bool hasWarning: {
        var m = node.scan_meta;
        return m && m.warnings && m.warnings.length > 0;
    }

    signal clicked()
    signal toggleDisabledClicked()

    height: 56
    color: hovered ? Qt.darker(Theme.color("surface"), 1.05) : "transparent"
    border.color: Theme.color("outline") || "#79747E"
    border.width: 1
    radius: Theme.radius

    RowLayout {
        anchors.fill: parent
        anchors.margins: 8
        spacing: 12

        // ⚠ 角标(有 warning 时)
        Label {
            visible: root.hasWarning
            text: "⚠"
            color: Theme.color("error") || "#B3261E"
            font.pixelSize: 18
        }

        // 包名 + 版本
        ColumnLayout {
            Layout.fillWidth: true
            spacing: 2
            Label {
                text: root.node.package || ""
                font.bold: true
                font.pixelSize: 14
            }
            Label {
                visible: !!root.node.version
                text: root.node.version || ""
                font.pixelSize: 11
                color: Theme.color("onSurfaceVariant") || "#79747E"
            }
        }

        // 状态
        Label {
            text: root.node.status === "disabled" ? qsTr("已禁用") : qsTr("启用中")
            color: root.node.status === "disabled"
                ? (Theme.color("onSurfaceVariant") || "#79747E")
                : (Theme.color("primary") || "#6750A4")
            font.pixelSize: 12
        }

        // 启停按钮
        Button {
            text: root.node.status === "disabled" ? qsTr("启用") : qsTr("禁用")
            onClicked: root.toggleDisabledClicked()
        }
    }

    MouseArea {
        anchors.fill: parent
        onClicked: root.clicked()
        hoverEnabled: true
    }
}