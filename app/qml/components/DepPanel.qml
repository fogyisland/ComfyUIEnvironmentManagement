import QtQuick 2.15
import QtQuick.Controls 2.15
import QtQuick.Layouts 1.15
import Manager 1.0

GroupBox {
    id: root
    title: qsTr("依赖分析 (%1 个冲突)").arg(conflictCount)

    property string currentEnvId: ""
    property var depList: []
    property var conflictList: []
    property bool globalCompatEnabled: false

    readonly property int conflictCount: conflictList.length

    signal rescanAllRequested()
    signal checkGlobalRequested()

    ColumnLayout {
        anchors.fill: parent
        spacing: 8

        RowLayout {
            Layout.fillWidth: true
            Button {
                text: qsTr("重新解析依赖")
                onClicked: root.rescanAllRequested()
            }
            Button {
                text: qsTr("检查全局已知冲突")
                enabled: root.globalCompatEnabled
                onClicked: root.checkGlobalRequested()
            }
            Item { Layout.fillWidth: true }
        }

        Label {
            visible: root.depList.length === 0
            text: qsTr("(尚无依赖记录 — 点上面的按钮解析)")
            color: Theme.color("outline")
        }

        Repeater {
            model: root.depList
            delegate: Label {
                Layout.fillWidth: true
                text: modelData.package + " → " + modelData.dep_name +
                      (modelData.dep_version_spec ? " " +
                       modelData.dep_version_spec : "")
                font.pixelSize: 12
            }
        }

        Label {
            visible: root.conflictCount > 0
            text: qsTr("⚠ %1 个冲突").arg(root.conflictCount)
            color: Theme.color("error")
            font.bold: true
        }
        Repeater {
            visible: root.conflictCount > 0
            model: root.conflictList
            delegate: Label {
                Layout.fillWidth: true
                wrapMode: Text.Wrap
                text: {
                    var pkg = modelData.node_ids.join(" / ");
                    return qsTr("冲突: %1 (%2)").arg(pkg).arg(
                        modelData.detail.dep_name || modelData.conflict_type);
                }
                color: Theme.color("error")
            }
        }
    }
}
