import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts

RowLayout {
    id: root
    property string path: ""
    property string fileFilter: "All files (*)"
    property string fileMode: "file"  // "file" or "directory"

    TextField {
        id: pathInput
        Layout.fillWidth: true
        text: root.path
        placeholderText: qsTr("选择路径...")
        onTextEdited: root.path = text
    }
    Button {
        text: qsTr("浏览...")
        onClicked: {
            if (root.fileMode === "directory") {
                // QtQuick.Dialogs 自 Qt 6.3+ 提供 FolderDialog
                const dlg = Qt.createQmlObject(
                    'import QtQuick.Dialogs; FolderDialog {}',
                    root)
                dlg.currentFolder = root.path ? "file:///" + root.path : ""
                dlg.accepted.connect(function() {
                    root.path = dlg.selectedFolder.toString().replace("file:///", "").replace("file://", "")
                })
                dlg.open()
            } else {
                const dlg = Qt.createQmlObject(
                    'import QtQuick.Dialogs; FileDialog { nameFilters: [' + root.fileFilter + '] }',
                    root)
                dlg.currentFolder = root.path ? "file:///" + root.path : ""
                dlg.accepted.connect(function() {
                    root.path = dlg.selectedFile.toString().replace("file:///", "").replace("file://", "")
                })
                dlg.open()
            }
        }
    }
}