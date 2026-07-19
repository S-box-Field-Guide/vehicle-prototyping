global using Sandbox;
global using System;
global using System.Collections.Generic;
global using System.Linq;
global using System.Threading.Tasks;
global using VehicleProto;
// Consume the vendored Vehicle Physics Kit (Libraries/fieldguide.vehiclephysics). The vehicle stack
// (VehicleController, VehicleWheel, Drivetrain, TireCurve, CarDefinition, VehicleFactory,
// VehicleCamera, EngineAudio, SkidAudio, DriveInputs, Units, and the DriveLayout/BodyStyle/AssistLevel
// enums) now lives in this namespace.
global using FieldGuide.VehiclePhysics;

// Both namespaces declare a CarDefinitions roster (the kit ships a blockout demo roster; the game
// keeps its own tuned roster with part-kit body manifests in CarRoster.cs). Current-namespace
// precedence already binds unqualified CarDefinitions to the game roster inside VehicleProto code,
// but that binding is invisible to the compiler when both rosters share the same shape, so pin it
// with an explicit alias: every game-side CarDefinitions resolves to the game roster regardless of
// the referring file's namespace. The kit compiles as its own assembly and never sees this alias.
global using CarDefinitions = VehicleProto.CarDefinitions;
