import QtQuick 2.15
import QtQuick.Controls 2.15
import QtQuick.Layouts 1.15
import Manager 1.0

Rectangle {
    id: root
    property bool busy: false
    visible: busy
    color: "#E7E0EC"
    radius: Theme.radius
    height: 32

    RowLayout {
        anchors.fill: parent
        anchors.margins: 8
        spacing: 8

        BusyIndicator {
            running: root.busy
            Layout.preferredWidth: 20
            Layout.preferredHeight: 20
        }
        Label {
            text: qsTr("正在扫描节点…")
            font.pixelSize: 12
        }
    }
}
