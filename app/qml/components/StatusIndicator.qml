import QtQuick
import QtQuick.Controls.Material

Rectangle {
    id: root
    property string status: "stopped"  // stopped / running / error
    width: 12
    height: 12
    radius: 6
    color: {
        if (status === "running") return "#4CAF50"
        if (status === "error") return "#F44336"
        return "#9E9E9E"
    }
}
