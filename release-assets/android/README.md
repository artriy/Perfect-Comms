# Android microphone permission

Perfect Comms captures Android audio through Unity's `Microphone` API. The final Among Us APK
must declare `android.permission.RECORD_AUDIO`, and the user must grant the runtime microphone
permission when Android asks.

`AndroidManifest.xml` in this directory is a merge fragment. Merge its `uses-permission` element
into the APK's real manifest before the APK is signed and installed. Copying the fragment beside
`PerfectCommsAndroid.dll` does not modify an already-built APK.

Release automation rejects an Android package when this fragment is missing or does not contain
the exact `android.permission.RECORD_AUDIO` declaration.
