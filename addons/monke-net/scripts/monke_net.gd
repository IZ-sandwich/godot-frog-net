@tool
extends EditorPlugin

var _open_logs_button: Button

func _enter_tree():
	add_autoload_singleton("MonkeNet", "res://addons/monke-net/scenes/MonkeNet.tscn")

	_open_logs_button = Button.new()
	_open_logs_button.text = "Open Latest Log"
	_open_logs_button.tooltip_text = "Open the OS file manager at the most recent Godot session log file (user://logs)."
	_open_logs_button.pressed.connect(_on_open_logs_pressed)
	add_control_to_container(EditorPlugin.CONTAINER_TOOLBAR, _open_logs_button)


func _exit_tree():
	if _open_logs_button:
		remove_control_from_container(EditorPlugin.CONTAINER_TOOLBAR, _open_logs_button)
		_open_logs_button.queue_free()
		_open_logs_button = null
	remove_autoload_singleton("MonkeNet")


func _on_open_logs_pressed() -> void:
	# Godot's default file logging writes to ProjectSettings["debug/file_logging/log_path"]
	# (default "user://logs/godot.log"). Resolve user:// to the OS-absolute path.
	var configured_path: String = ProjectSettings.get_setting("debug/file_logging/log_path", "user://logs/godot.log")
	var log_dir: String = ProjectSettings.globalize_path(configured_path).get_base_dir()

	if not DirAccess.dir_exists_absolute(log_dir):
		push_warning("Monke-Net: log directory does not exist yet (%s). Run the game once to generate a log." % log_dir)
		return

	# Find the newest file in the log dir — that's the latest session.
	var newest_path := ""
	var newest_mtime: int = 0
	var dir := DirAccess.open(log_dir)
	if dir != null:
		dir.list_dir_begin()
		var entry := dir.get_next()
		while entry != "":
			if not dir.current_is_dir():
				var full := log_dir.path_join(entry)
				var mtime := FileAccess.get_modified_time(full)
				if mtime > newest_mtime:
					newest_mtime = mtime
					newest_path = full
			entry = dir.get_next()
		dir.list_dir_end()

	# OS.shell_show_in_file_manager opens the OS file manager and selects the target.
	# Falls back to opening the log directory if there is no file yet.
	var target := newest_path if not newest_path.is_empty() else log_dir
	print("Monke-Net: revealing log in file manager: ", target)
	OS.shell_show_in_file_manager(target)
