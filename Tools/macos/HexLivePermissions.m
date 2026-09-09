#import <AVFoundation/AVFoundation.h>

// §160. No capture session is created here: FMOD remains the sole recorder.
__attribute__((visibility("default"))) int HexLiveMicrophoneStatus(void) {
    return (int)[AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeAudio];
}

__attribute__((visibility("default"))) void HexLiveRequestMicrophone(void) {
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        dispatch_async(dispatch_get_main_queue(), ^{
            if ([AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeAudio] == AVAuthorizationStatusNotDetermined) {
                [AVCaptureDevice requestAccessForMediaType:AVMediaTypeAudio completionHandler:^(BOOL granted) {}];
            }
        });
    });
}
