using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CPUOptimization2;

// finish-gen fires the layer popup, then we cover the screen for the cull.
// the popup uses unscaled time, so it fades out while nobody can see it.
internal static class LayerAlertHold
{
	struct Note
	{
		public string Text;
		public bool Important;
	}

	static bool _hold;
	static bool _playing;
	static readonly List<Note> _queued = new List<Note>();
	static bool _hasFollowup;
	static Note _followup;
	static float _followupAt;
	static float _swallowUntil;

	internal static void Apply(Harmony harmony)
	{
		harmony.Patch(
			AccessTools.Method(typeof(PlayerCamera), "DoAlert"),
			prefix: new HarmonyMethod(typeof(LayerAlertHold), nameof(DoAlertPrefix)));
	}

	internal static void Begin()
	{
		_hold = true;
		_queued.Clear();
		_hasFollowup = false;
		_swallowUntil = 0f;

		PlayerCamera cam = PlayerCamera.main;
		if (cam == null || cam.alertText == null)
			return;
		if (FieldAccess.Get<float>(cam, "alertTime") <= 0.05f)
			return;

		bool important = cam.alertBack != null && cam.alertBack.rectTransform.anchoredPosition.y > -200f;
		_queued.Add(new Note { Text = cam.alertText.text, Important = important });
		FieldAccess.Set(cam, "alertTime", 0f);
	}

	internal static void Release()
	{
		_hold = false;
		if (_queued.Count == 0)
		{
			_swallowUntil = 0f;
			return;
		}

		Note first = _queued[0];
		_hasFollowup = _queued.Count >= 2;
		if (_hasFollowup)
			_followup = _queued[1];
		_queued.Clear();

		// title now. modifier line is 6s after that, same gap the game uses.
		// if the delayed call hasnt landed yet, catch it until the original 6s is up.
		_followupAt = Time.unscaledTime + 6f;
		_swallowUntil = _hasFollowup ? 0f : _followupAt + 0.05f;
		Play(first);
	}

	internal static void Tick()
	{
		if (!_hasFollowup || Time.unscaledTime < _followupAt)
			return;
		Note note = _followup;
		_hasFollowup = false;
		_swallowUntil = 0f;
		Play(note);
	}

	internal static void Clear()
	{
		_hold = false;
		_playing = false;
		_queued.Clear();
		_hasFollowup = false;
		_swallowUntil = 0f;
	}

	static bool DoAlertPrefix(string text, bool important)
	{
		if (_playing)
			return true;
		if (_hold)
		{
			_queued.Add(new Note { Text = text, Important = important });
			return false;
		}
		if (_swallowUntil > 0f && Time.unscaledTime < _swallowUntil
			&& text != null && text.IndexOf("color=", System.StringComparison.OrdinalIgnoreCase) >= 0)
		{
			_followup = new Note { Text = text, Important = important };
			_hasFollowup = true;
			_swallowUntil = 0f;
			return false;
		}
		return true;
	}

	static void Play(Note note)
	{
		PlayerCamera cam = PlayerCamera.main;
		if (cam == null || string.IsNullOrEmpty(note.Text))
			return;
		_playing = true;
		try
		{
			cam.DoAlert(note.Text, note.Important);
		}
		finally
		{
			_playing = false;
		}
	}
}
