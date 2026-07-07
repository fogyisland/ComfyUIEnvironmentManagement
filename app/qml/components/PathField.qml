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
                // 不能在 createQmlObject 字符串里拼 fileFilter(含 (),* 等),
                // QML 6 解析器会拒绝。先用纯 QML 创建,再用 setProperty 注入。
                const dlg = Qt.createQmlObject(
                    'import QtQuick.Dialogs; FileDialog {}',
                    root)
                dlg.nameFilters = [root.fileFilter]
                dlg.currentFolder = root.path ? "file:///" + root.path : ""
                dlg.accepted.connect(function() {
                    root.path = dlg.selectedFile.toString().replace("file:///", "").replace("file://", "")
                })
                dlg.open()
            }
        }
    }
}