// Idempotently author §160 direct-reply focus: master -8 dB, short fades.
// Core voice channels bypass the Studio bus and keep their authored gain.
(function () {
    var snapshot = studio.project.lookup("snapshot:/AgentReplyFocus");
    if (!snapshot) {
        snapshot = studio.project.create("Snapshot");
        snapshot.name = "AgentReplyFocus";
    }
    var master = studio.project.workspace.mixer.masterBus;
    var scopedVolume = null;
    var properties = snapshot.snapshotProperties || [];
    for (var i = 0; i < properties.length; ++i) {
        if (properties[i].automatableObject === master &&
            properties[i].propertyName === "volume") scopedVolume = properties[i];
    }
    if (!scopedVolume) {
        scopedVolume = studio.project.create("SnapshotProperty");
        scopedVolume.snapshot = snapshot;
        scopedVolume.automatableObject = master;
        scopedVolume.propertyName = "volume";
    }
    scopedVolume.value = -8;

    var masterTrack = null;
    var tracks = snapshot.snapshotTracks || [];
    for (var j = 0; j < tracks.length; ++j)
        if (tracks[j].mixerStrip === master) masterTrack = tracks[j];
    if (!masterTrack) {
        masterTrack = studio.project.create("SnapshotTrack");
        masterTrack.snapshot = snapshot;
        masterTrack.mixerStrip = master;
    }

    var controls = snapshot.automatableProperties;
    var modulator = null;
    var modulators = controls.modulators || [];
    for (var k = 0; k < modulators.length; ++k) {
        if (modulators[k].isOfType("ADSRModulator") &&
            modulators[k].nameOfPropertyBeingModulated === "snapshotIntensity")
            modulator = modulators[k];
    }
    if (!modulator)
        modulator = controls.addModulator("ADSRModulator", "snapshotIntensity");
    modulator.initialValue = 0;
    modulator.attackTime = 0.15;
    modulator.peakValue = 1;
    modulator.holdTime = 0;
    modulator.decayTime = 0;
    modulator.sustainValue = 1;
    modulator.releaseTime = 0.15;
    modulator.finalValue = 0;
    if (!studio.project.save()) throw new Error("FMOD Studio refused to save AgentReplyFocus.");
    console.log("AgentReplyFocus configured: Master Bus -8 dB, 150 ms attack/release.");
}());
