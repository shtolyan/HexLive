// Idempotently author the §159 microphone-capture ducking snapshot.
// Run with:
//   fmodstudiocl -script ../Scripts/configure_voice_capture_duck.js HexLive.fspro
(function () {
    var snapshot = studio.project.lookup("snapshot:/VoiceCaptureDuck");
    if (!snapshot) {
        snapshot = studio.project.create("Snapshot");
        snapshot.name = "VoiceCaptureDuck";
    }

    var master = studio.project.workspace.mixer.masterBus;
    var scopedVolume = null;
    var properties = snapshot.snapshotProperties || [];
    for (var i = 0; i < properties.length; ++i) {
        if (properties[i].automatableObject === master &&
            properties[i].propertyName === "volume") {
            scopedVolume = properties[i];
            break;
        }
    }
    if (!scopedVolume) {
        scopedVolume = studio.project.create("SnapshotProperty");
        scopedVolume.snapshot = snapshot;
        scopedVolume.automatableObject = master;
        scopedVolume.propertyName = "volume";
    }
    scopedVolume.value = -18;

    var masterTrack = null;
    var tracks = snapshot.snapshotTracks || [];
    for (var j = 0; j < tracks.length; ++j) {
        if (tracks[j].mixerStrip === master) {
            masterTrack = tracks[j];
            break;
        }
    }
    if (!masterTrack) {
        masterTrack = studio.project.create("SnapshotTrack");
        masterTrack.snapshot = snapshot;
        masterTrack.mixerStrip = master;
    }

    // Snapshot intensity is the correct place for a smooth code-triggered fade.
    var controls = snapshot.automatableProperties;
    var modulator = null;
    var modulators = controls.modulators || [];
    for (var k = 0; k < modulators.length; ++k) {
        if (modulators[k].isOfType("ADSRModulator") &&
            modulators[k].nameOfPropertyBeingModulated === "snapshotIntensity") {
            modulator = modulators[k];
            break;
        }
    }
    if (!modulator) {
        modulator = controls.addModulator("ADSRModulator", "snapshotIntensity");
    }
    modulator.initialValue = 0;
    modulator.attackTime = 0.15;
    modulator.peakValue = 1;
    modulator.holdTime = 0;
    modulator.decayTime = 0;
    modulator.sustainValue = 1;
    modulator.releaseTime = 0.15;
    modulator.finalValue = 0;

    if (!snapshot.isValid || !scopedVolume.isValid || !masterTrack.isValid ||
        !modulator.isValid) {
        throw new Error("VoiceCaptureDuck authoring produced an invalid FMOD object graph.");
    }

    if (!studio.project.save()) {
        throw new Error("FMOD Studio refused to save VoiceCaptureDuck.");
    }
    console.log("VoiceCaptureDuck configured: Master Bus -18 dB, 150 ms attack/release.");
}());
