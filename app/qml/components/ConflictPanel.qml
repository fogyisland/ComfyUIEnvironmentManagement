import QtQuick 2.15
import QtQuick.Controls 2.15
import QtQuick.Layouts 1.15

Rectangle {
    id: root
    property var conflicts: []              // list of Conflict dicts
    property string currentEnvId: ""

    signal resolveClicked(string conflictId)
    signal ignoreClicked(string conflictId)
    signal disableNodeClicked(string nodeId)
    signal viewNodeClicked(string nodeId)

    visible: conflicts.length > 0
    color: Theme.color("errorContainer") || "#FCE8E6"
    radius: Theme.radius
    implicitHeight: col.implicitHeight + 24

    ColumnLayout {
        id: col
        anchors.fill: parent
        anchors.margins: 12
        spacing: 8

        Label {
            text: qsTr("⚠ %1 个冲突").arg(root.conflicts.length)
            font.pixelSize: 16
            font.bold: true
            color: Theme.color("error") || "#B3261E"
        }

        Repeater {
            model: root.conflicts
            delegate: Rectangle {
                width: parent.width
                height: row.implicitHeight + 16
                color: "transparent"

                RowLayout {
                    id: row
                    anchors.fill: parent
                    spacing: 8

                    Label {
                        Layout.fillWidth: true
                        text: {
                            if (modelData.conflict_type === "duplicate_class") {
                                return qsTr("类 \"%1\" 被 %2 个节点提供")
                                    .arg(modelData.detail.class)
                                    .arg(modelData.node_ids.length);
                            }
                            return qsTr("冲突: %1").arg(modelData.conflict_type);
                        }
                        wrapMode: Text.Wrap
                    }

                    Button {
                        text: qsTr("禁用其一")
                        onClicked: {
                            // 简化:禁用 node_ids 里第一个
                            root.disableNodeClicked(modelData.node_ids[0]);
                        }
                    }
                    Button {
                        text: qsTr("查看详情")
                        onClicked: root.viewNodeClicked(modelData.node_ids[0])
                    }
                    Button {
                        text: qsTr("忽略")
                        onClicked: root.ignoreClicked(modelData.id)
                    }
                }
            }
        }
    }
}