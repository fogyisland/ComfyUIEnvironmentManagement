import QtQuick 2.15
import QtQuick.Controls 2.15
import QtQuick.Layouts 1.15
import Manager 1.0

Dialog {
    id: root
    modal: true
    title: qsTr("版本历史 — %1").arg(packageName)
    width: 600
    height: 400

    property string packageName: ""
    property string currentEnvId: ""
    property var history: []
    signal rollbackRequested(string historyId)

    function load(envId, pkg) {
        root.currentEnvId = envId;
        root.packageName = pkg;
        var r = nodeBridge.listVersionHistory(envId, pkg, 50);
        if (r.ok) {
            root.history = r.value;
        } else {
            root.history = [];
        }
    }

    contentItem: ColumnLayout {
        Label {
            text: qsTr("%1 条历史记录").arg(root.history.length)
            font.bold: true
        }
        ListView {
            Layout.fillWidth: true
            Layout.fillHeight: true
            model: root.history
            clip: true
            delegate: Rectangle {
                width: ListView.view.width
                height: row.implicitHeight + 8
                color: "transparent"
                RowLayout {
                    id: row
                    anchors.fill: parent
                    anchors.margins: 4
                    Label {
                        text: modelData.performed_at + "  " +
                              modelData.action + "  " +
                              (modelData.version_before || "?") + " → " +
                              (modelData.version_after || "?")
                        Layout.fillWidth: true
                        font.pixelSize: 11
                    }
                    Label {
                        text: modelData.result
                        color: modelData.result === "success"
                               ? Theme.color("primary")
                               : Theme.color("error")
                    }
                    Button {
                        text: qsTr("回滚")
                        enabled: modelData.version_before &&
                                 modelData.action !== "rollback"
                        onClicked: root.rollbackRequested(modelData.id)
                    }
                }
            }
        }
    }

    footer: DialogButtonBox {
        Button {
            text: qsTr("关闭")
            DialogButtonBox.buttonRole: DialogButtonBox.RejectRole
            onClicked: root.reject()
        }
    }
}
