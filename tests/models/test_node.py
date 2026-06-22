from comfy_mgr.models.node import Node

def test_node_minimal_construction():
    n = Node(id="x", name="X", repo_url="https://github.com/x/y", local_path="D:/catalog/nodes/X")
    assert n.id == "x"
    assert n.description == ""
    assert n.current_version is None