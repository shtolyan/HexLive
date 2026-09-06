import importlib.util
import pathlib
import queue
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("admin_agent", ROOT / "Server/HexLive.AdminAgentHost/admin_agent.py")
agent = importlib.util.module_from_spec(spec); spec.loader.exec_module(agent)

class FakeMcp:
    def __init__(self, refuse=False): self.calls = []; self.refuse = refuse
    def tool(self, name, args):
        self.calls.append((name, dict(args)))
        if name == "admin_next_turn": return {"idle": True}
        return {"accepted": not self.refuse, "reason": "ConfirmationRequired" if self.refuse else ""}

class AdminAgentTests(unittest.TestCase):
    def run_turn(self, calls, refusal=False):
        codex = agent.Codex.__new__(agent.Codex); codex.messages = queue.Queue(); sent = []; requests = []
        codex.send = sent.append
        def request(method, params):
            requests.append((method, params)); return {"thread": {"id": "thread"}, "turn": {"id": "turn"}}
        codex.request = request
        for i, (name, call_id) in enumerate(calls):
            codex.messages.put({"id": 100+i, "method": "item/tool/call", "params": {
                "tool": name, "callId": call_id, "threadId": "thread", "arguments": {"kind": "heal", "npcId": 7, "turnId": "forged"}}})
        codex.messages.put({"method": "item/completed", "params": {"item": {"type": "agentMessage", "text": "Готово"}}})
        codex.messages.put({"method": "turn/completed", "params": {"turn": {"status": "completed"}}})
        mcp = FakeMcp(refusal)
        result = codex.turn(mcp, {"turnId": "0123456789abcdef0123456789abcdef", "text": "Вылечи", "selectedNpcId": 7},
            [{"name": "admin_execute", "description": "execute", "inputSchema": {"type": "object", "properties": {"turnId": {}, "operationId": {}, "kind": {}}}}],
            "gpt-6-astra", "/tmp", [])
        return mcp, sent, requests, result
    def test_tool_identity_is_owned_by_host_and_repeat_is_deduplicated(self):
        mcp, sent, requests, result = self.run_turn([("admin_execute", "same"), ("admin_execute", "same")])
        executions = [args for name,args in mcp.calls if name == "admin_execute"]
        self.assertEqual(len(executions), 1)
        self.assertEqual(executions[0]["turnId"], "0123456789abcdef0123456789abcdef")
        self.assertEqual(len(executions[0]["operationId"]), 32)
        self.assertEqual(result, "Готово")
        dynamic = requests[0][1]["dynamicTools"][0]
        self.assertEqual(dynamic["type"], "function")
        self.assertNotIn("turnId", dynamic["inputSchema"]["properties"])
    def test_confirmation_stops_later_actions(self):
        mcp, _, _, _ = self.run_turn([("admin_execute", "a"), ("admin_execute", "b")], True)
        self.assertEqual(sum(name == "admin_execute" for name,_ in mcp.calls), 1)
    def test_unknown_tool_never_reaches_server(self):
        mcp, _, _, _ = self.run_turn([("shell", "a"), ("admin_confirm", "b")])
        self.assertTrue(all(name == "admin_next_turn" for name,_ in mcp.calls))
    def test_url_userinfo_cannot_spoof_loopback(self):
        with self.assertRaises(ValueError): agent.Mcp("http://127.0.0.1:80@public.example/mcp", "secret")
    def test_redirect_cannot_forward_bearer(self):
        with self.assertRaises(ValueError): agent.NoRedirect().redirect_request(None, None, 302, "", {}, "https://other.example")
    def test_remote_plain_http_is_refused_before_network(self):
        with self.assertRaises(ValueError): agent.Mcp("http://public.example/mcp", "secret")

if __name__ == "__main__": unittest.main()
