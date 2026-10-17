"""Launch-time helper for the standalone Blender preview; no project mutation."""
import bpy,json
from pathlib import Path
D=Path(__file__).resolve().parent/'Clothing_Primal'
def confirm():
 (D/'preview_open_status.json').write_text(json.dumps({'file':bpy.data.filepath,'frame':bpy.context.scene.frame_current,'playing':any(w.screen.is_animation_playing for w in bpy.context.window_manager.windows)}));return None
def play():
 for window in bpy.context.window_manager.windows:
  if not window.screen.is_animation_playing:
   bpy.ops.screen.animation_play({'window':window,'screen':window.screen})
  bpy.app.timers.register(confirm,first_interval=1.5);return None
 return .5
bpy.app.timers.register(play,first_interval=2)
