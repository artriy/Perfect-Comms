use super::*;

mod aggregate_device;
mod api;
mod backlog;
mod device_change;
mod device_property;
mod interfaces;
mod manual;
mod parallel;
mod sync_callback;
mod tone;
mod utils;

#[test]
fn input_callback_logger_preserves_bounded_fifo() {
    let mut logger = InputCallbackLogger::new();
    assert!(logger.is_empty());
    for bytes in 0..17 {
        logger.push(InputCallbackData {
            bytes,
            rendered_frames: bytes + 1,
            total_available: bytes as usize + 2,
            channels: 1,
            num_buf: 1,
        });
    }
    assert!(!logger.is_empty());
    for bytes in 0..16 {
        let data = logger.pop().unwrap();
        assert_eq!(data.bytes, bytes);
        assert_eq!(data.rendered_frames, bytes + 1);
        assert_eq!(data.total_available, bytes as usize + 2);
        assert_eq!(data.channels, 1);
        assert_eq!(data.num_buf, 1);
    }
    assert!(logger.pop().is_none());
    assert!(logger.is_empty());
}
