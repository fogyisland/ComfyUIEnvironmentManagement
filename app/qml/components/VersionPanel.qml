import QtQuick 2.15
import QtQuick.Controls 2.15
import QtQuick.Layouts 1.15

GroupBox {
    id: root
    title: qsTr("节点版本 (%1)").arg(versionList.length)

    property string currentEnvId: ""
    property var versionList: []
    property bool hasUpdatable: false

    signal refreshRequested()
    signal upgradeAllRequested()
    signal historyRequested(string package)

    function refresh() {
        root.refreshRequested();
    }

    ColumnLayout {
        anchors.fill: parent
        spacing: 8

        RowLayout {
            Layout.fillWidth: true
            Button {
                text: qsTr("刷新版本状态")
                onClicked: root.refresh()
            }
            Button {
                text: qsTr("全部升级")
                enabled: root.hasUpdatable
                onClicked: root.upgradeAllRequested()
            }
            Item { Layout.fillWidth: true }
        }

        Repeater {
            model: root.versionList
            delegate: Rectangle {
                Layout.fillWidth: true
                height: row.implicitHeight + 12
                color: "transparent"
                border.color: Theme.color("outline")
                radius: 4

                RowLayout {
                    id: row
                    anchors.fill: parent
                    anchors.margins: 6
                    spacing: 8

                    Label {
                        text: modelData.package + " @ " +
                              (modelData.current_sha_short || "?")
                        Layout.fillWidth: true
                    }
                    Label {
                        text: modelData.locked ? qsTr("🔒 锁定") : ""
                        color: Theme.color("primary")
                    }
                    Label {
                        visible: modelData.has_update
                        text: qsTr("有更新")
                        color: Theme.color("primary")
                    }
                    Button {
                        text: qsTr("升级")
                        enabled: !modelData.locked && modelData.has_remote
                        onClicked: appContext.node_bridge.upgradeNode(
                            root.currentEnvId, modelData.package, "")
                    }
                    Button {
                        text: modelData.locked ? qsTr("解锁") : qsTr("锁定")
                        onClicked: {
                            if (modelData.locked) {
                                appContext.node_bridge.unlockVersion(
                                    root.currentEnvId, modelData.package);
                            } else {
                                appContext.node_bridge.lockVersion(
                                    root.currentEnvId, modelData.package);
                            }
                        }
                    }
                    Button {
                        text: qsTr("历史")
                        onClicked: root.historyRequested(modelData.package)
                    }
                }
            }
        }

        Label {
            visible: root.versionList.length === 0
            text: qsTr("(尚无节点 — 请先扫描)")
            color: Theme.color("outline")
        }
    }
}
