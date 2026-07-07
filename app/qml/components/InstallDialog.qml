import QtQuick 2.15
import QtQuick.Controls 2.15
import QtQuick.Layouts 1.15
import Manager 1.0

Dialog {
    id: root
    modal: true
    standardButtons: Dialog.NoButton
    title: qsTr("安装节点")
    width: 480

    property var catalogEntry: ({})
    property var envList: []
    property int selectedEnvIndex: 0
    property bool busyIndicatorRunning: false
    signal installRequested(string envId)

    onBusyIndicatorRunningChanged: {
        busyIndicator.running = root.busyIndicatorRunning;
    }

    onOpened: {
        root.busyIndicatorRunning = false;
    }

    contentItem: ColumnLayout {
        spacing: 12

        Label {
            text: catalogEntry.name || catalogEntry.id || ""
            font.pixelSize: 18
            font.bold: true
        }
        Label {
            text: catalogEntry.author ? qsTr("作者: %1").arg(catalogEntry.author) : ""
            color: Theme.color("outline")
        }
        Label {
            visible: catalogEntry.stars !== undefined
            text: qsTr("⭐ %1").arg(catalogEntry.stars)
        }
        Label {
            Layout.fillWidth: true
            wrapMode: Text.Wrap
            text: catalogEntry.description || ""
        }
        Label {
            Layout.fillWidth: true
            text: catalogEntry.repo || ""
            color: Theme.color("outline")
            font.pixelSize: 10
        }

        RowLayout {
            Layout.fillWidth: true
            Label { text: qsTr("目标 env:") }
            ComboBox {
                id: envCombo
                Layout.fillWidth: true
                model: root.envList
                currentIndex: root.selectedEnvIndex
                onActivated: root.selectedEnvIndex = currentIndex
            }
        }

        BusyIndicator {
            id: busyIndicator
            running: false
            Layout.alignment: Qt.AlignHCenter
        }
    }

    footer: DialogButtonBox {
        Button {
            text: qsTr("取消")
            DialogButtonBox.buttonRole: DialogButtonBox.RejectRole
            onClicked: root.reject()
        }
        Button {
            text: qsTr("安装")
            enabled: envCombo.count > 0
            DialogButtonBox.buttonRole: DialogButtonBox.AcceptRole
            onClicked: {
                if (envCombo.count === 0) return;
                busyIndicator.running = true;
                root.installRequested(root.envList[root.selectedEnvIndex].id ||
                                      root.envList[root.selectedEnvIndex]);
            }
        }
    }
}
