global using Sandbox;
global using System;
global using System.Collections.Generic;
global using System.Linq;
global using System.Threading.Tasks;
global using VehicleProto;
// Consume the vendored Vehicle Physics Kit (Libraries/fieldguide.vehiclephysics). The vehicle stack
// (VehicleController, VehicleWheel, Drivetrain, TireCurve, CarDefinition, VehicleFactory,
// VehicleCamera, EngineAudio, SkidAudio, DriveInputs, Units, and the DriveLayout/BodyStyle/AssistLevel
// enums) now lives in this namespace. Game-side types that share a kit name are disambiguated by
// current-namespace precedence: VehicleProto.CarDefinitions (the game roster in CarRoster.cs) wins
// over the kit's demo CarDefinitions for unqualified references in VehicleProto code.
global using FieldGuide.VehiclePhysics;
