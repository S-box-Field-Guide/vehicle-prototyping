using System;

namespace VehicleProto;

/// <summary>
/// LIVE DEBUG INSTRUMENT (2026-07-21, ramp-hitch hunt round 6). Rides ON the car root (the
/// chassis Rigidbody's GameObject) and records every collision callback the chassis receives,
/// for <see cref="RampTraceRecorder"/> to drain once per fixed tick. Round-5 offline analysis
/// found the car's raw pose RATCHETS on the ramp face (per-tick advance alternating
/// 0.63 m / 0.23 m at 35 m/s, a 40% displacement deficit) while rigidbody velocity stays
/// glassy-smooth — the signature of contact-driven position clamping, suspected ghost
/// (speculative) contacts against the internal faces of the overlapping kicker segment boxes.
/// This probe answers WHO is in contact during the climb, and with what normals.
/// Created by the recorder at capture start, destroyed at dump; inert otherwise.
/// </summary>
public sealed class RampContactProbe : Component, Component.ICollisionListener
{
	/// <summary>Contacts noted since the recorder last drained (fires during the physics stage,
	/// so the recorder reads them on the NEXT tick's OnFixedUpdate — one tick of latency).</summary>
	public int Count { get; private set; }

	/// <summary>Normal of the most WALL-LIKE contact since last drain (smallest |n.z|): the
	/// ghost-contact hypothesis predicts near-horizontal normals from segment box internal faces.</summary>
	public Vector3 WorstNormal { get; private set; }

	/// <summary>GameObject name of the most wall-like contact's other collider.</summary>
	public string WorstOther { get; private set; } = "";

	float _worstAbsZ = float.MaxValue;

	public void OnCollisionStart( Collision c ) => Note( c );
	public void OnCollisionUpdate( Collision c ) => Note( c );
	public void OnCollisionStop( CollisionStop c ) { }

	void Note( Collision c )
	{
		Count++;
		var n = c.Contact.Normal;
		float absZ = MathF.Abs( n.z );
		if ( absZ < _worstAbsZ )
		{
			_worstAbsZ = absZ;
			WorstNormal = n;
			WorstOther = c.Other.GameObject?.Name ?? "?";
		}
	}

	/// <summary>Read-and-reset, called once per fixed tick by the recorder.</summary>
	public (int count, Vector3 normal, string other) Drain()
	{
		var result = (Count, WorstNormal, WorstOther);
		Count = 0;
		_worstAbsZ = float.MaxValue;
		WorstNormal = Vector3.Zero;
		WorstOther = "";
		return result;
	}
}
